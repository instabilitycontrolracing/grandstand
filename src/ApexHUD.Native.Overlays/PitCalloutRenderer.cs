using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Port 08 — the Pit Callout. Slides down from above when any car enters or
/// exits pit road, showing the state badge (PIT IN / PIT OUT), the driver's
/// position, number, name, the car model (on entry) or rejoin-position delta
/// (on exit), and the manufacturer logo. Multiple events queue and play one
/// after another at ~4 s each.
/// <para>Fixes two legacy bugs: the manufacturer logo never showed (wrong
/// global reference) and the state-badge label never updated. Native build
/// uses <see cref="ManufacturerBrand.Resolve"/> against the bundled 34 PNGs
/// and renders the badge label/verb explicitly per event.</para>
/// </summary>
public sealed class PitCalloutRenderer : IDisposable
{
    public const float CardWidth = 760f;
    public const float CardHeight = 120f;

    // Timing (legacy lines 51-66 + 293-294 verbatim).
    const double EnterAnimMs = 1000;
    const double ExitAnimMs = 600;
    const double VisibleMs = 4000;

    static readonly SKColor BgSolid        = ParseHex("#0A0C14", 242); // legacy rgba(10,12,20,0.95)
    static readonly SKColor TextPrimary    = ParseHex(BroadcastPalette.TextPrimary);
    static readonly SKColor TextSecondary  = ParseHex(BroadcastPalette.TextSecondary);
    static readonly SKColor TextTertiary   = ParseHex(BroadcastPalette.TextTertiary);
    static readonly SKColor BadgeBg        = new(255, 255, 255, 13); // rgba(255,255,255,0.05)
    static readonly SKColor BadgeBorder    = new(255, 255, 255, 20); // rgba(255,255,255,0.08)
    static readonly SKColor NumberChipBg   = new(255, 255, 255, 20); // rgba(255,255,255,0.08)
    static readonly SKColor InColor        = ParseHex("#FBBF24"); // amber for PIT IN
    static readonly SKColor OutColor       = ParseHex("#34D399"); // green for PIT OUT
    static readonly SKColor LossColor      = ParseHex("#F87171");
    static readonly SKColor GainColor      = ParseHex("#34D399");

    enum EventType { In, Out }

    readonly record struct PitEvent(
        EventType Type,
        int Position,
        int? PositionDelta, // null for IN; for OUT: positive = lost positions, negative = gained
        string CarNumber,
        string DriverName,
        string CarModel,
        string Brand,
        string ClassColor); // hex string

    // Per-CarIdx state from prior frames so we can detect transitions.
    readonly Dictionary<int, bool> prevPit = new();
    readonly Dictionary<int, bool> prevOnTrack = new();   // loaded in AND off pit road last sample
    readonly Dictionary<int, int> entryPosition = new();
    bool firstObservation = true;

    // Event queue + currently-showing event.
    readonly Queue<PitEvent> queue = new();
    PitEvent? showing;
    double showStartedAtMs;
    double leaveStartedAtMs = -1;

    // Logo cache (mirrors the tower / featured / grid pattern).
    readonly Dictionary<string, SKBitmap?> logoCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True while the callout is rendering anything.</summary>
    public bool IsActive => showing != null;

    public bool Draw(SKCanvas canvas, float originX, float originY,
                     RaceSnapshot snapshot, double nowMs)
    {
        if (snapshot.IsConnected)
            DetectPitTransitions(snapshot);

        AdvanceQueue(nowMs);

        float alpha = ComputeAlpha(nowMs);
        if (alpha <= 0.005f || showing is not { } e) return false;

        canvas.Save();
        canvas.Translate(originX, originY + ComputeTranslateY(nowMs));
        canvas.Scale(ComputeScale(nowMs), ComputeScale(nowMs), CardWidth / 2f, CardHeight / 2f);

        var card = new SKRect(0, 0, CardWidth, CardHeight);
        var classColor = ParseHex(string.IsNullOrEmpty(e.ClassColor) ? "#6366F1" : e.ClassColor);

        DrawChrome(canvas, card, classColor, alpha);
        DrawStateBadge(canvas, card, e, alpha);
        DrawIdentity(canvas, card, e, alpha);
        DrawBrand(canvas, card, e, alpha);

        canvas.Restore();
        return true;
    }

    // ----- detection -----

    void DetectPitTransitions(RaceSnapshot snap)
    {
        // First observation: seed the prev-state map without firing anything
        // (legacy did the same — avoids spamming events at session join).
        if (firstObservation)
        {
            foreach (var cls in snap.ClassLeaderboard)
            foreach (var d in cls.Drivers)
            {
                prevPit[d.CarIdx] = d.IsInPit;
                if (d.IsInPit && d.Position > 0)
                    entryPosition[d.CarIdx] = d.Position;
            }
            firstObservation = false;
            return;
        }

        // 2026-10-18 (Angelo): before the green, drivers LOADING INTO the session
        // pop from "not in the world" straight into their pit stall, which read as
        // a pit entry and spammed callouts. Two guards:
        //  1. nothing fires until notifications are armed (race: green flag;
        //     practice/qualify: immediately) - state is still tracked meanwhile;
        //  2. a pit ENTRY only counts if the car was loaded in AND out on track
        //     on the previous sample (a car appearing in its stall never was).
        bool armed = PresentationState.NotificationsArmed;
        foreach (var cls in snap.ClassLeaderboard)
        foreach (var d in cls.Drivers)
        {
            bool wasOnPit = prevPit.TryGetValue(d.CarIdx, out var prev) && prev;
            bool nowOnPit = d.IsInPit;
            bool wasOnTrack = prevOnTrack.TryGetValue(d.CarIdx, out var pot) && pot;
            prevOnTrack[d.CarIdx] = d.IsConnected && !d.IsInPit;
            if (nowOnPit == wasOnPit) continue;

            if (d.Position <= 0 || !armed) { prevPit[d.CarIdx] = nowOnPit; continue; }

            if (!wasOnPit && nowOnPit && !wasOnTrack) { prevPit[d.CarIdx] = nowOnPit; continue; }

            if (!wasOnPit && nowOnPit)
            {
                entryPosition[d.CarIdx] = d.Position;
                queue.Enqueue(new PitEvent(
                    Type: EventType.In,
                    Position: d.Position,
                    PositionDelta: null,
                    CarNumber: string.IsNullOrEmpty(d.CarNumber) ? "—" : d.CarNumber,
                    DriverName: NameFormat.Broadcast(d),
                    CarModel: (d.CarName ?? string.Empty).ToUpperInvariant(),
                    Brand: d.CarBrand ?? string.Empty,
                    ClassColor: ResolveClassColor(d)));
            }
            else // exited pit
            {
                int? delta = entryPosition.TryGetValue(d.CarIdx, out var ep)
                    ? d.Position - ep
                    : (int?)null;
                entryPosition.Remove(d.CarIdx);
                queue.Enqueue(new PitEvent(
                    Type: EventType.Out,
                    Position: d.Position,
                    PositionDelta: delta,
                    CarNumber: string.IsNullOrEmpty(d.CarNumber) ? "—" : d.CarNumber,
                    DriverName: NameFormat.Broadcast(d),
                    CarModel: (d.CarName ?? string.Empty).ToUpperInvariant(),
                    Brand: d.CarBrand ?? string.Empty,
                    ClassColor: ResolveClassColor(d)));
            }
            prevPit[d.CarIdx] = nowOnPit;
        }
    }

    static string ResolveClassColor(Driver d)
    {
        if (!string.IsNullOrEmpty(d.ClassColor)) return d.ClassColor;
        return BroadcastPalette.Class(d.ClassName);
    }

    // ----- queue + state machine -----

    void AdvanceQueue(double nowMs)
    {
        // Start exit when dwell elapses.
        if (showing != null && leaveStartedAtMs < 0 && nowMs - showStartedAtMs >= EnterAnimMs + VisibleMs)
            leaveStartedAtMs = nowMs;

        // Clear once exit finishes; pop next event.
        if (showing != null && leaveStartedAtMs >= 0 && nowMs - leaveStartedAtMs >= ExitAnimMs)
        {
            showing = null;
            leaveStartedAtMs = -1;
        }

        if (showing == null && queue.Count > 0)
        {
            showing = queue.Dequeue();
            showStartedAtMs = nowMs;
            leaveStartedAtMs = -1;
            StorySound.PlayBroadcast("pit_callout");
        }
    }

    float ComputeAlpha(double nowMs)
    {
        if (showing == null) return 0;
        if (leaveStartedAtMs < 0)
        {
            double t = Math.Clamp((nowMs - showStartedAtMs) / EnterAnimMs, 0, 1);
            return (float)Math.Min(1, t / 0.6); // opacity reaches 1 at 60% of enter
        }
        double tl = Math.Clamp((nowMs - leaveStartedAtMs) / ExitAnimMs, 0, 1);
        return (float)(1 - tl);
    }

    float ComputeTranslateY(double nowMs)
    {
        if (showing == null) return 0;
        if (leaveStartedAtMs < 0)
        {
            // Enter: -160% → 8% (60%) → 0%
            double t = Math.Clamp((nowMs - showStartedAtMs) / EnterAnimMs, 0, 1);
            double y;
            if (t < 0.6)
            {
                double k = t / 0.6;
                y = -1.6 + (-1.6 - 0.08) * -EaseOutQuint(k); // == -1.6 + (1.6 + 0.08) * eased
            }
            else
            {
                double k = (t - 0.6) / 0.4;
                y = 0.08 + (0 - 0.08) * k;
            }
            return (float)(y * CardHeight);
        }
        double tl = Math.Clamp((nowMs - leaveStartedAtMs) / ExitAnimMs, 0, 1);
        return (float)(-0.5 * EaseInQuad(tl) * CardHeight);
    }

    float ComputeScale(double nowMs)
    {
        if (showing == null) return 1f;
        if (leaveStartedAtMs < 0)
        {
            double t = Math.Clamp((nowMs - showStartedAtMs) / EnterAnimMs, 0, 1);
            if (t < 0.6) return (float)(0.96 + (1.01 - 0.96) * EaseOutQuint(t / 0.6));
            return (float)(1.01 + (1.0 - 1.01) * ((t - 0.6) / 0.4));
        }
        double tl = Math.Clamp((nowMs - leaveStartedAtMs) / ExitAnimMs, 0, 1);
        return (float)(1.0 + (0.94 - 1.0) * EaseInQuad(tl));
    }

    static double EaseOutQuint(double t) => BroadcastTiming.EaseOutQuint(t);
    static double EaseInQuad(double t) => t * t;

    // ----- drawing -----

    void DrawChrome(SKCanvas c, SKRect card, SKColor classColor, float alpha)
    {
        // Solid dark background (legacy stays opaque — this is a callout, not a glass card).
        using var bg = new SKPaint { IsAntialias = true, Color = BgSolid.WithAlpha((byte)(BgSolid.Alpha * alpha)) };
        c.DrawRect(card, bg);
        Theme.SkinNow(c, card, ThemeSurface.Popup, alpha);   // #601: the broadcast theme (nothing on Default)

        // Inset white hairline border (legacy box-shadow inset).
        using var border = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            Color = new SKColor(255, 255, 255, (byte)(15 * alpha)),
        };
        c.DrawRect(card, border);

        // Left class-color stripe (5px) with a glow halo.
        var stripe = new SKRect(card.Left, card.Top, card.Left + 5, card.Bottom);
        using var stripeShader = SKShader.CreateLinearGradient(
            new SKPoint(card.Left, 0), new SKPoint(card.Left + 16, 0),
            new[]
            {
                classColor.WithAlpha((byte)(170 * alpha)),
                classColor.WithAlpha(0),
            },
            new[] { 0f, 1f }, SKShaderTileMode.Clamp);
        using var halo = new SKPaint { IsAntialias = true, Shader = stripeShader };
        c.DrawRect(new SKRect(card.Left, card.Top, card.Left + 16, card.Bottom), halo);
        using var stripePaint = new SKPaint { IsAntialias = true, Color = classColor.WithAlpha((byte)(255 * alpha)) };
        c.DrawRect(stripe, stripePaint);

        // Bottom 2px class-color gradient.
        using var bottomShader = SKShader.CreateLinearGradient(
            new SKPoint(card.Left, 0), new SKPoint(card.Right, 0),
            new[]
            {
                classColor.WithAlpha(0),
                classColor.WithAlpha((byte)(153 * alpha)),
                classColor.WithAlpha((byte)(153 * alpha)),
                classColor.WithAlpha(0),
            },
            new[] { 0f, 0.3f, 0.7f, 1f }, SKShaderTileMode.Clamp);
        using var bottomBar = new SKPaint { IsAntialias = true, Shader = bottomShader };
        c.DrawRect(new SKRect(card.Left, card.Bottom - 2, card.Right, card.Bottom), bottomBar);
    }

    void DrawStateBadge(SKCanvas c, SKRect card, PitEvent e, float alpha)
    {
        // State badge column: 110 px wide, starting after the left stripe + padding.
        const float colW = 110f;
        const float padLeft = 22f;
        var badge = new SKRect(card.Left + padLeft, card.Top + 16, card.Left + padLeft + colW, card.Bottom - 16);

        using var bg = new SKPaint { IsAntialias = true, Color = BadgeBg.WithAlpha((byte)(BadgeBg.Alpha * alpha)) };
        c.DrawRect(badge, bg);
        using var border = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            Color = BadgeBorder.WithAlpha((byte)(BadgeBorder.Alpha * alpha)),
        };
        c.DrawRect(badge, border);

        // Label "STATUS" — small tracked tertiary
        using var labelPaint = new SKPaint
        {
            IsAntialias = true,
            Color = TextTertiary.WithAlpha((byte)(230 * alpha)), // #340: was 10 px @0.7 (legacy)
            Typeface = Fonts.Inter.Bold,
            TextSize = 13,
            TextAlign = SKTextAlign.Center,
        };
        float labelCenter = (badge.Left + badge.Right) / 2f;
        DrawTrackedCenter(c, "STATUS", labelCenter, badge.Top + 23, labelPaint, 0.22f);

        // Verb "PIT IN" or "PIT OUT" coloured per event
        var verbColor = e.Type == EventType.In ? InColor : OutColor;
        using var verbPaint = new SKPaint
        {
            IsAntialias = true,
            Color = verbColor.WithAlpha((byte)(255 * alpha)),
            Typeface = Fonts.Inter.Bold,
            TextSize = 22,
            TextAlign = SKTextAlign.Center,
        };
        // Drop-shadow / glow halo (cheap approximation)
        using var glowPaint = new SKPaint
        {
            IsAntialias = true,
            Color = verbColor.WithAlpha((byte)(115 * alpha)),
            Typeface = Fonts.Inter.Bold,
            TextSize = 22,
            TextAlign = SKTextAlign.Center,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 8),
        };
        var verb = e.Type == EventType.In ? "PIT IN" : "PIT OUT";
        c.DrawText(verb, labelCenter, badge.Bottom - 18, glowPaint);
        c.DrawText(verb, labelCenter, badge.Bottom - 18, verbPaint);
    }

    void DrawIdentity(SKCanvas c, SKRect card, PitEvent e, float alpha)
    {
        const float padLeft = 22f + 110f + 22f; // left pad + badge col + gap
        float x = card.Left + padLeft;
        float right = card.Right - 76f - 26f - 22f; // brand col + right pad + gap

        // --- Top row: P{n}  #{NUM}  NAME ---
        float topRowY = card.Top + 16 + 22; // baseline

        // Position with small "P" prefix
        using var posPrefix = new SKPaint
        {
            IsAntialias = true,
            Color = TextTertiary.WithAlpha((byte)(255 * alpha)),
            Typeface = Fonts.Inter.Bold,
            TextSize = 14,
        };
        using var posNum = new SKPaint
        {
            IsAntialias = true,
            Color = TextPrimary.WithAlpha((byte)(255 * alpha)),
            Typeface = Fonts.Inter.Bold,
            TextSize = 22,
        };
        c.DrawText("P", x, topRowY, posPrefix);
        float posX = x + posPrefix.MeasureText("P") + 2;
        var posStr = e.Position.ToString(CultureInfo.InvariantCulture);
        c.DrawText(posStr, posX, topRowY, posNum);
        float cursor = posX + posNum.MeasureText(posStr) + 12;

        // Number chip
        using var numChipText = new SKPaint
        {
            IsAntialias = true,
            Color = TextPrimary.WithAlpha((byte)(255 * alpha)),
            Typeface = Fonts.Inter.Bold,
            TextSize = 14,   // #340: was 12
        };
        var numW = numChipText.MeasureText(e.CarNumber);
        var chipRect = new SKRect(cursor, topRowY - 14, cursor + numW + 16, topRowY + 4);
        using var chipBg = new SKPaint { IsAntialias = true, Color = NumberChipBg.WithAlpha((byte)(NumberChipBg.Alpha * alpha)) };
        c.DrawRect(chipRect, chipBg);
        using var chipBorder = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            Color = new SKColor(255, 255, 255, (byte)(25 * alpha)),
        };
        c.DrawRect(chipRect, chipBorder);
        c.DrawText(e.CarNumber, chipRect.Left + 8, topRowY, numChipText);
        cursor = chipRect.Right + 12;

        // Driver name (Cormorant Bold Italic 28 — we have only Bold, not Italic-Bold loaded,
        // so use Bold and apply skew transform to fake italic for the legacy look).
        using var namePaint = new SKPaint
        {
            IsAntialias = true,
            Color = TextPrimary.WithAlpha((byte)(255 * alpha)),
            Typeface = Fonts.CormorantGaramond.Bold,
            TextSize = 28,
            TextSkewX = -0.18f, // fake italic — matches the legacy font-style:italic
        };
        var maxNameW = right - cursor - 8;
        var name = EllipsiseToWidth(e.DriverName, namePaint, maxNameW);
        c.DrawText(name, cursor, topRowY + 2, namePaint);

        // --- Detail row underneath ---
        float detailY = topRowY + 24;
        using var detailLabel = new SKPaint
        {
            IsAntialias = true,
            Color = TextSecondary.WithAlpha((byte)(242 * alpha)), // #340: was 11 px @0.85 (legacy)
            Typeface = Fonts.Inter.Bold,
            TextSize = 13,
        };

        if (e.Type == EventType.In)
        {
            var detail = string.IsNullOrEmpty(e.CarModel) ? "INTO PITS" : $"INTO PITS  ·  {e.CarModel}";
            DrawTracked(c, detail, x, detailY, detailLabel, 0.14f);
        }
        else
        {
            var leading = $"OUT  ·  REJOINS P{e.Position}  ·  ";
            DrawTracked(c, leading, x, detailY, detailLabel, 0.14f);
            float leadW = MeasureTracked(leading, detailLabel, 0.14f);

            var (msg, color) = FormatDelta(e.PositionDelta);
            using var deltaPaint = new SKPaint
            {
                IsAntialias = true,
                Color = color.WithAlpha((byte)(255 * alpha)),
                Typeface = Fonts.Inter.Bold,
                TextSize = 13,
            };
            DrawTracked(c, msg, x + leadW, detailY, deltaPaint, 0.14f);
        }
    }

    static (string, SKColor) FormatDelta(int? delta)
    {
        if (delta == null || delta == 0) return ("NO POSITIONS CHANGED", TextTertiary);
        int v = delta.Value;
        if (v > 0)
        {
            var s = v == 1 ? "POSITION" : "POSITIONS";
            return ($"LOST {v} {s}", LossColor);
        }
        else
        {
            var g = -v;
            var s = g == 1 ? "POSITION" : "POSITIONS";
            return ($"GAINED {g} {s}", GainColor);
        }
    }

    void DrawBrand(SKCanvas c, SKRect card, PitEvent e, float alpha)
    {
        const float w = 76f, h = 76f;
        var rect = new SKRect(card.Right - 22 - w, (card.Top + card.Bottom - h) / 2f,
                              card.Right - 22, (card.Top + card.Bottom + h) / 2f);

        var slug = ManufacturerBrand.Resolve(e.Brand);
        if (slug == null) return; // legacy bug: it never showed; here we just skip when unknown

        var bmp = LoadLogo(slug);
        if (bmp == null) return;

        // Aspect-fit inside the rect
        float bw = bmp.Width, bh = bmp.Height;
        float scale = Math.Min(w / bw, h / bh);
        float drawW = bw * scale, drawH = bh * scale;
        var dst = new SKRect(
            (rect.Left + rect.Right - drawW) / 2f,
            (rect.Top + rect.Bottom - drawH) / 2f,
            (rect.Left + rect.Right + drawW) / 2f,
            (rect.Top + rect.Bottom + drawH) / 2f);

        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.High,
            Color = new SKColor(255, 255, 255, (byte)(255 * alpha)),
        };
        c.DrawBitmap(bmp, dst, paint);
    }

    SKBitmap? LoadLogo(string slug)
    {
        if (logoCache.TryGetValue(slug, out var cached)) return cached;
        SKBitmap? loaded = null;
        try { loaded = Assets.LoadBitmap("logos", "manufacturers", slug + ".png"); }
        catch { loaded = null; }
        logoCache[slug] = loaded;
        return loaded;
    }

    // ----- helpers (letter spacing, ellipsing, hex) -----

    static void DrawTracked(SKCanvas c, string text, float x, float y, SKPaint paint, float trackingEm)
    {
        float track = paint.TextSize * trackingEm;
        var widths = paint.GetGlyphWidths(text);
        float cursor = x;
        for (int i = 0; i < text.Length; i++)
        {
            c.DrawText(text[i].ToString(), cursor, y, paint);
            cursor += widths[i] + track;
        }
    }

    static void DrawTrackedCenter(SKCanvas c, string text, float xCenter, float y, SKPaint paint, float trackingEm)
    {
        float w = MeasureTracked(text, paint, trackingEm);
        float track = paint.TextSize * trackingEm;
        var widths = paint.GetGlyphWidths(text);
        var prevAlign = paint.TextAlign;
        paint.TextAlign = SKTextAlign.Left;
        try
        {
            float cursor = xCenter - w / 2f;
            for (int i = 0; i < text.Length; i++)
            {
                c.DrawText(text[i].ToString(), cursor, y, paint);
                cursor += widths[i] + track;
            }
        }
        finally { paint.TextAlign = prevAlign; }
    }

    static float MeasureTracked(string text, SKPaint paint, float trackingEm)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        float track = paint.TextSize * trackingEm;
        var widths = paint.GetGlyphWidths(text);
        float w = 0;
        for (int i = 0; i < widths.Length; i++) w += widths[i] + track;
        return w - track;
    }

    static string EllipsiseToWidth(string text, SKPaint paint, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0) return text ?? string.Empty;
        if (paint.MeasureText(text) <= maxWidth) return text;
        var s = text;
        while (s.Length > 1 && paint.MeasureText(s + "…") > maxWidth)
            s = s[..^1];
        return s + "…";
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
        foreach (var b in logoCache.Values) b?.Dispose();
        logoCache.Clear();
    }
}
