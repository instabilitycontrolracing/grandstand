using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Port 08 — the Fastest Lap callout. Animated lower-third that slides up when
/// any driver sets a new session-best lap, showing the badge, driver identity,
/// the lap time, and the delta vs the previous session best. Auto-dismisses
/// after 5.5 s. Drives off <see cref="RaceSnapshot.ClassLeaderboard"/> by
/// walking every class (fixing the legacy single-class bug) and labels the
/// class of the driver who set the lap, not the spectated car's class.
/// </summary>
public sealed class FastestLapRenderer : IDisposable
{
    public const float CardWidth = 720f;
    public const float CardHeight = 110f;

    // Timing (matches legacy fastest-lap.html lines 388-399 verbatim).
    const double EnterAnimMs = 720;
    const double ExitAnimMs = 600;
    const double DwellMs = 5500;
    const double CooldownMs = 4000;

    // Improvement threshold to dodge float noise (legacy 0.001 s).
    const double ImprovementEpsilonS = 0.001;

    static readonly SKColor BgGlass        = ParseHex(BroadcastPalette.TowerGlassBase, 209);
    static readonly SKColor TextPrimary    = ParseHex(BroadcastPalette.TextPrimary);
    static readonly SKColor TextTertiary   = ParseHex(BroadcastPalette.TextTertiary);
    static readonly SKColor StatusPurple   = ParseHex(BroadcastPalette.FastestPurple);
    static readonly SKColor StatusGreen    = ParseHex("#4ADE80");

    // Session-best tracking. Reset on track change.
    string? lastTrackName;
    double sessionBestSeconds = double.PositiveInfinity;

    // Currently-shown callout content (frozen at trigger time).
    string showName = string.Empty;
    string showNumber = string.Empty;
    string showBrand = string.Empty;
    string showClass = string.Empty;
    double showTimeSeconds;
    double showDeltaSeconds;
    bool showHasDelta;

    // Visibility state machine.
    bool isVisible;
    double shownAtMs;
    double leaveStartedAtMs = -1;
    double lastTriggerAtMs = double.NegativeInfinity;

    /// <summary>True while the callout is rendering anything (visible or leaving).</summary>
    public bool IsActive => isVisible || leaveStartedAtMs >= 0;

    /// <summary>
    /// Drive one frame: detects a new session best across all classes, advances
    /// the visibility state machine, and paints the callout. Returns true if
    /// any pixels were drawn.
    /// </summary>
    public bool Draw(SKCanvas canvas, float originX, float originY,
                     RaceSnapshot snapshot, double nowMs)
    {
        if (snapshot.IsConnected)
            DetectNewBest(snapshot, nowMs);

        UpdateStateMachine(nowMs);

        float alpha = ComputeAlpha(nowMs);
        if (alpha <= 0.005f) return false;

        canvas.Save();
        canvas.Translate(originX, originY + ComputeTranslateY(nowMs));
        canvas.Scale(ComputeScale(nowMs), ComputeScale(nowMs), CardWidth / 2f, CardHeight / 2f);

        var card = new SKRect(0, 0, CardWidth, CardHeight);
        DrawChrome(canvas, card, alpha, nowMs);
        DrawLeft(canvas, card, alpha);
        DrawRight(canvas, card, alpha);

        canvas.Restore();
        return true;
    }

    // ----- detection -----

    void DetectNewBest(RaceSnapshot snap, double nowMs)
    {
        // Reset on a new track OR a new session: the qualifying best must not carry into the race
        // (it made the race's fastest-lap callout almost never fire).
        string key = snap.TrackBroadcastName + "|" + snap.SessionTypeName;
        if (key != lastTrackName)
        {
            lastTrackName = key;
            sessionBestSeconds = double.PositiveInfinity;
            return;
        }

        Driver? best = null;
        double bestSec = double.PositiveInfinity;

        // Walk every class — legacy bug was Class00-only.
        foreach (var cls in snap.ClassLeaderboard)
        foreach (var d in cls.Drivers)
        {
            if (!d.IsConnected) continue;
            if (d.BestLapTime <= 0) continue;
            if (d.BestLapTime < bestSec)
            {
                bestSec = d.BestLapTime;
                best = d;
            }
        }

        if (best == null || double.IsPositiveInfinity(bestSec)) return;

        // First observation: record but don't trigger (avoids spam at session join).
        if (double.IsPositiveInfinity(sessionBestSeconds))
        {
            sessionBestSeconds = bestSec;
            return;
        }

        if (bestSec >= sessionBestSeconds - ImprovementEpsilonS) return;

        var delta = bestSec - sessionBestSeconds;
        sessionBestSeconds = bestSec;

        // 2026-10-18: no notifications before the green (race) - record only.
        if (!PresentationState.NotificationsArmed) return;

        // Cooldown + already-showing block (legacy drops overlapping events).
        if (isVisible || leaveStartedAtMs >= 0) return;
        if (nowMs - lastTriggerAtMs < CooldownMs) return;

        Trigger(best, bestSec, delta, nowMs);
    }

    void Trigger(Driver d, double timeS, double deltaS, double nowMs)
    {
        showName = NameFormat.Broadcast(d);
        showNumber = string.IsNullOrEmpty(d.CarNumber) ? "—" : "#" + d.CarNumber;
        showBrand = (d.CarBrand ?? string.Empty).ToUpperInvariant();
        // Label the class of the driver who set the lap — fixes the legacy bug
        // where it used the spectated car's CarId.
        showClass = (d.ClassName ?? string.Empty).ToUpperInvariant();
        showTimeSeconds = timeS;
        showDeltaSeconds = deltaS;
        showHasDelta = double.IsFinite(deltaS);

        isVisible = true;
        shownAtMs = nowMs;
        leaveStartedAtMs = -1;
        lastTriggerAtMs = nowMs;
        StorySound.PlayBroadcast("fastest_lap");
        HighlightLog.Add("fastest", $"Fastest lap{(showClass.Length > 0 ? " " + showClass : "")}: {showName}", d.CarIdx);   // #487
    }

    // ----- state machine -----

    void UpdateStateMachine(double nowMs)
    {
        if (!isVisible && leaveStartedAtMs < 0) return;
        if (isVisible && nowMs - shownAtMs >= DwellMs)
        {
            isVisible = false;
            leaveStartedAtMs = nowMs;
        }
        if (leaveStartedAtMs >= 0 && nowMs - leaveStartedAtMs >= ExitAnimMs)
        {
            leaveStartedAtMs = -1;
        }
    }

    float ComputeAlpha(double nowMs)
    {
        if (isVisible)
        {
            // Enter: 0 → 1 across EnterAnimMs (the slide also fades in).
            double t = Math.Clamp((nowMs - shownAtMs) / EnterAnimMs, 0, 1);
            // Match legacy keyframe: opacity goes 0→1 by 60% of duration.
            return (float)Math.Min(1, t / 0.6);
        }
        if (leaveStartedAtMs >= 0)
        {
            double t = Math.Clamp((nowMs - leaveStartedAtMs) / ExitAnimMs, 0, 1);
            return (float)(1 - t);
        }
        return 0;
    }

    float ComputeTranslateY(double nowMs)
    {
        if (isVisible)
        {
            // Legacy keyframe: 160% → -4% (overshoot at 60%) → 0%.
            double t = Math.Clamp((nowMs - shownAtMs) / EnterAnimMs, 0, 1);
            double y;
            if (t < 0.6)
            {
                double k = t / 0.6;
                y = 1.6 - (1.6 - (-0.04)) * EaseOutQuint(k);
            }
            else
            {
                double k = (t - 0.6) / 0.4;
                y = -0.04 + (0 - (-0.04)) * k;
            }
            return (float)(y * CardHeight);
        }
        if (leaveStartedAtMs >= 0)
        {
            double t = Math.Clamp((nowMs - leaveStartedAtMs) / ExitAnimMs, 0, 1);
            return (float)(EaseInQuad(t) * 0.6 * CardHeight);
        }
        return 0;
    }

    float ComputeScale(double nowMs)
    {
        if (!isVisible) return 1f;
        double t = Math.Clamp((nowMs - shownAtMs) / EnterAnimMs, 0, 1);
        // Legacy: 0.96 → 1.012 (at 60%) → 1.
        if (t < 0.6)
        {
            double k = t / 0.6;
            return (float)(0.96 + (1.012 - 0.96) * EaseOutQuint(k));
        }
        double k2 = (t - 0.6) / 0.4;
        return (float)(1.012 + (1.0 - 1.012) * k2);
    }

    static double EaseOutQuint(double t) => BroadcastTiming.EaseOutQuint(t);

    static double EaseInQuad(double t) => t * t;

    // ----- drawing -----

    void DrawChrome(SKCanvas c, SKRect card, float alpha, double nowMs)
    {
        // Deep-glass background, same family as the other lower-thirds.
        using var glass = new SKPaint
        {
            IsAntialias = true,
            Color = BgGlass.WithAlpha((byte)(BgGlass.Alpha * alpha)),
        };
        c.DrawRect(card, glass);
        Theme.Skin(c, card, ThemeSurface.Popup, nowMs, alpha);   // #601: the broadcast theme (nothing on Default)

        // Purple top accent (2px) with a sweep-in scaleX driven by the same
        // entrance progress as the card.
        float sweep = Math.Clamp((float)((nowMs - shownAtMs - 200) / 600.0), 0f, 1f);
        if (!isVisible) sweep = 1f;
        float barW = CardWidth * sweep;
        float barCenter = card.Left + CardWidth / 2f;
        var barRect = new SKRect(barCenter - barW / 2f, card.Top, barCenter + barW / 2f, card.Top + 2f);
        using var barShader = SKShader.CreateLinearGradient(
            new SKPoint(barRect.Left, 0), new SKPoint(barRect.Right, 0),
            new[]
            {
                StatusPurple.WithAlpha(0),
                StatusPurple.WithAlpha((byte)(255 * alpha)),
                StatusPurple.WithAlpha((byte)(255 * alpha)),
                StatusPurple.WithAlpha(0),
            },
            new[] { 0f, 0.18f, 0.82f, 1f },
            SKShaderTileMode.Clamp);
        using var bar = new SKPaint { IsAntialias = true, Shader = barShader };
        c.DrawRect(barRect, bar);

        // Purple aura on the right (radial gradient behind the lap time).
        var auraCenter = new SKPoint(card.Right - 60, card.Top - 40);
        using var auraShader = SKShader.CreateRadialGradient(
            auraCenter, 200f,
            new[]
            {
                StatusPurple.WithAlpha((byte)(115 * alpha)),
                StatusPurple.WithAlpha((byte)(46 * alpha)),
                StatusPurple.WithAlpha(0),
            },
            new[] { 0f, 0.35f, 1f },
            SKShaderTileMode.Clamp);
        using var aura = new SKPaint { IsAntialias = true, Shader = auraShader };
        c.DrawRect(card, aura);
    }

    void DrawLeft(SKCanvas c, SKRect card, float alpha)
    {
        float x = card.Left + 28;
        float yMid = card.Top + 14 + 41; // top padding + half of card content

        // "FASTEST LAP" badge with a bolt glyph in front.
        using var badgePaint = new SKPaint
        {
            IsAntialias = true,
            Color = StatusPurple.WithAlpha((byte)(255 * alpha)),
            Typeface = Fonts.Inter.Bold,
            TextSize = 14,   // #340: was 12
        };
        const float bolt = 12f;
        float boltTop = yMid - 32;
        DrawBolt(c, x, boltTop, bolt, StatusPurple.WithAlpha((byte)(255 * alpha)));
        DrawTracked(c, "FASTEST LAP", x + bolt + 10, boltTop + bolt, badgePaint, 0.32f);

        // Driver row: #NUM   NAME   META
        using var numPaint = new SKPaint
        {
            IsAntialias = true,
            Color = TextPrimary.WithAlpha((byte)(255 * alpha)),
            Typeface = Fonts.CormorantGaramond.Bold,
            TextSize = 26,
        };
        float rowY = boltTop + bolt + 20;
        float cursor = x;
        c.DrawText(showNumber, cursor, rowY + 26, numPaint);
        cursor += numPaint.MeasureText(showNumber) + 14;

        using var namePaint = new SKPaint
        {
            IsAntialias = true,
            Color = TextPrimary.WithAlpha((byte)(255 * alpha)),
            Typeface = Fonts.Inter.Bold,
            TextSize = 24,
        };
        // Ellipsise the name so it never collides with the right column.
        float rightLimit = card.Right - 240; // reserve right-hand area for the lap time
        float metaW = MetaWidth(alpha);
        float nameMax = rightLimit - cursor - metaW - 24;
        var name = EllipsiseToWidth(showName, namePaint, nameMax);
        c.DrawText(name, cursor, rowY + 26, namePaint);
        cursor += namePaint.MeasureText(name) + 16;

        // META on the far right of the row (CLASS · BRAND).
        var meta = ComposeMeta();
        if (!string.IsNullOrEmpty(meta))
        {
            using var metaPaint = new SKPaint
            {
                IsAntialias = true,
                Color = TextTertiary.WithAlpha((byte)(255 * alpha)),
                Typeface = Fonts.Inter.Medium,
                TextSize = 13,   // #340: was 10
            };
            DrawTracked(c, meta, cursor, rowY + 23, metaPaint, 0.16f);
        }
    }

    float MetaWidth(float alpha)
    {
        var meta = ComposeMeta();
        if (string.IsNullOrEmpty(meta)) return 0;
        using var metaPaint = new SKPaint
        {
            IsAntialias = true,
            Typeface = Fonts.Inter.Medium,
            TextSize = 13,
        };
        return MeasureTracked(meta, metaPaint, 0.16f);
    }

    string ComposeMeta()
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrEmpty(showClass)) parts.Add(showClass);
        if (!string.IsNullOrEmpty(showBrand)) parts.Add(showBrand);
        return string.Join("  ·  ", parts);
    }

    void DrawRight(SKCanvas c, SKRect card, float alpha)
    {
        float rightEdge = card.Right - 28;

        // Lap time (the focal point). Cormorant Bold 48 px right-aligned.
        using var timePaint = new SKPaint
        {
            IsAntialias = true,
            Color = TextPrimary.WithAlpha((byte)(255 * alpha)),
            Typeface = Fonts.CormorantGaramond.Bold,
            TextSize = 48,
            TextAlign = SKTextAlign.Right,
        };
        var timeStr = LapTimeFormat.Lap(showTimeSeconds);
        float timeY = card.Top + 14 + 48;
        c.DrawText(timeStr, rightEdge, timeY, timePaint);

        if (!showHasDelta) return;

        // Delta row: ▼ −X.XXX   vs Previous
        double abs = Math.Abs(showDeltaSeconds);
        bool isFaster = showDeltaSeconds < 0;
        var deltaColor = StatusGreen.WithAlpha((byte)(255 * alpha));
        using var arrowPaint = new SKPaint
        {
            IsAntialias = true,
            Color = deltaColor,
            Typeface = Fonts.Inter.SemiBold,
            TextSize = 11,   // #340: was 9
            TextAlign = SKTextAlign.Right,
        };
        using var valPaint = new SKPaint
        {
            IsAntialias = true,
            Color = deltaColor,
            Typeface = Fonts.Inter.SemiBold,
            TextSize = 15,   // #340: was 13
            TextAlign = SKTextAlign.Right,
        };
        using var lblPaint = new SKPaint
        {
            IsAntialias = true,
            Color = TextTertiary.WithAlpha((byte)(255 * alpha)),
            Typeface = Fonts.Inter.Medium,
            TextSize = 13,   // #340: was 9
            TextAlign = SKTextAlign.Right,
        };

        // Lay out right-to-left: "vs Previous" then value then arrow.
        float deltaY = timeY + 22;
        float cursor = rightEdge;
        var lblStr = "vs Previous";
        var lblW = MeasureTracked(lblStr, lblPaint, 0.12f);
        DrawTrackedRight(c, lblStr, cursor, deltaY, lblPaint, 0.12f);
        cursor -= lblW + 8;

        var valStr = (isFaster ? "−" : "+") + abs.ToString("0.000", CultureInfo.InvariantCulture);
        c.DrawText(valStr, cursor, deltaY, valPaint);
        cursor -= valPaint.MeasureText(valStr) + 6;

        c.DrawText(isFaster ? "▼" : "▲", cursor, deltaY - 1, arrowPaint);
    }

    static void DrawBolt(SKCanvas c, float x, float y, float size, SKColor color)
    {
        using var p = new SKPaint { IsAntialias = true, Color = color };
        // Simplified lightning bolt — same silhouette as the legacy clip-path.
        var path = new SKPath();
        float w = size, h = size + 2;
        path.MoveTo(x + 0.45f * w, y + 0.00f * h);
        path.LineTo(x + 1.00f * w, y + 0.00f * h);
        path.LineTo(x + 0.55f * w, y + 0.42f * h);
        path.LineTo(x + 1.00f * w, y + 0.42f * h);
        path.LineTo(x + 0.00f * w, y + 1.00f * h);
        path.LineTo(x + 0.45f * w, y + 0.58f * h);
        path.LineTo(x + 0.05f * w, y + 0.58f * h);
        path.Close();
        c.DrawPath(path, p);
        path.Dispose();
    }

    // Letter-spacing emulation — same approach as SessionBarRenderer
    // (SkiaSharp 2.88 has no built-in letter-spacing).
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

    static void DrawTrackedRight(SKCanvas c, string text, float xRight, float y, SKPaint paint, float trackingEm)
    {
        float track = paint.TextSize * trackingEm;
        float w = MeasureTracked(text, paint, trackingEm);
        var left = xRight - w;
        var widths = paint.GetGlyphWidths(text);
        var prevAlign = paint.TextAlign;
        paint.TextAlign = SKTextAlign.Left;
        try
        {
            float cursor = left;
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

    public void Dispose() { }
}
