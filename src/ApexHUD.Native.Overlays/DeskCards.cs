using System.Globalization;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>2026-11-06 (producer desk): one row of a table card. Delta: places gained (+) / lost (-) for the arrow.</summary>
public sealed record DeskRow(string Pos, string Name, string Value, int Delta = 0, string Accent = "", string Sub = "");
/// <summary>A producer card: "table" (rows), "compare" (two drivers side by side) or "graph" (gap over laps).</summary>
public sealed record DeskCard(int Id, string Kind, string Eyebrow, string Title, DeskRow[] Rows, string HeadA = "", string HeadB = "",
    (string Label, string A, string B)[]? Compare = null, float[]? Series = null, string Foot = "");
/// <summary>A lower third: commentator name tag, driver focus, driver change.</summary>
public sealed record NameTag(int Id, string Eyebrow, string Line1, string Line2, string Accent = "");

/// <summary>What the producer has put on air (set by the host, read on the render thread). Until = Environment.TickCount64.</summary>
public static class DeskBoard
{
    public static volatile DeskCard? Card; public static long CardUntil;
    public static volatile NameTag? Tag; public static long TagUntil;
    public static bool CardWanted => Card != null && Environment.TickCount64 < Interlocked.Read(ref CardUntil);
    public static bool TagWanted => Tag != null && Environment.TickCount64 < Interlocked.Read(ref TagUntil);
    /// <summary>The name tag sits where the featured driver card does: the card steps aside while it's up.</summary>
    public static volatile bool TagUp;
    public static void ShowCard(DeskCard c, double seconds) { Card = c; Interlocked.Exchange(ref CardUntil, Environment.TickCount64 + (long)(seconds * 1000)); }
    public static void ShowTag(NameTag t, double seconds) { Tag = t; Interlocked.Exchange(ref TagUntil, Environment.TickCount64 + (long)(seconds * 1000)); }
    /// <summary>2026-11-06: a director replay's tag is up (it stays on screen while the rest of the graphics step aside).</summary>
    public static bool ReplayTag => TagWanted && Tag is { } rt && rt.Eyebrow.Contains("REPLAY", StringComparison.Ordinal);
    public static bool IsReplayTagShown => Tag is { } rt && rt.Eyebrow.Contains("REPLAY", StringComparison.Ordinal);
    public static void EndReplayTag() { if (IsReplayTagShown) Interlocked.Exchange(ref TagUntil, 0); }
    public static void Clear() { Interlocked.Exchange(ref CardUntil, 0); Interlocked.Exchange(ref TagUntil, 0); }
}

/// <summary>Shared look: the deep navy glass with the gold top line (same as the bet card and the latest plate).</summary>
internal static class DeskLook
{
    public static SKColor GoldHi => !Theme.Applies(ThemeSurface.Card) ? new SKColor(0xFF, 0xD8, 0x76) : Theme.Current.Accent;
    public static SKColor GoldMid => !Theme.Applies(ThemeSurface.Card) ? new SKColor(0xC9, 0xA9, 0x61) : Theme.Current.Accent;
    public static SKColor GoldLo => !Theme.Applies(ThemeSurface.Card) ? new SKColor(0xB8, 0x96, 0x4C) : Theme.Current.Accent;
    public static readonly SKColor Ivory = new(252, 250, 245), Txt2 = new(0xD3, 0xD9, 0xE3), Up = new(0x2E, 0xC4, 0x7A), Down = new(0xE0, 0x4F, 0x5B);
    static readonly float[] StopsLine = { 0f, 0.55f, 1f };

    public static void Glass(SKCanvas c, float w, float h, float e, double nowMs, SKPaint shaded, ShaderSlot glass, ShaderSlot line, ShaderSlot bloom)
    {
        byte A(float x) => (byte)Math.Clamp(255f * x * e, 0f, 255f);
        shaded.Shader = glass.Linear(new SKPoint(0, 0), new SKPoint(0, h), [new SKColor(14, 20, 34, 232), new SKColor(8, 12, 22, 242)], default);
        shaded.Color = new SKColor(0, 0, 0, A(1f));
        c.DrawRect(new SKRect(0, 0, w, h), shaded);
        shaded.Shader = bloom.Radial(new SKPoint(0, 0), 300f, [GoldMid.WithAlpha(30), GoldMid.WithAlpha(0)], default);
        c.DrawRect(new SKRect(0, 0, w, h), shaded);
        shaded.Shader = null;
        Theme.Skin(c, new SKRect(0, 0, w, h), ThemeSurface.Card, nowMs, e);
        shaded.Shader = line.Linear(new SKPoint(0, 0), new SKPoint(w, 0), [GoldLo, GoldHi, GoldHi.WithAlpha(0)], StopsLine);
        c.DrawRect(new SKRect(0, 0, w, 2f), shaded);
        shaded.Shader = null;
    }

    public static SKColor Hex(string hex, SKColor fallback) => hex.Length > 0 && SKColor.TryParse(hex.StartsWith('#') ? hex : "#" + hex, out var c) ? c : fallback;
}

/// <summary>The producer card on the right (table / head-to-head / gap graph), 500 wide under the session bar.</summary>
public sealed class DeskCardRenderer
{
    public const float W = 500f, Right = 1896f, Top = 150f, RowH = 34f, HeadH = 72f, WatchRowH = 78f;
    float k; double lastMs = -1; DeskCard? shown;
    public bool Active => k > 0.002f;
    public bool Fading => k > 0.002f && k < 0.998f;
    public SKRect LastRect { get; private set; }
    readonly SKPaint eyeP = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 10f };
    readonly SKPaint titleP = new() { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 26f };
    readonly SKPaint rowP = new() { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 19f };
    readonly SKPaint rowR = new() { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 19f, TextAlign = SKTextAlign.Right };
    readonly SKPaint rowC = new() { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 19f, TextAlign = SKTextAlign.Center };
    readonly SKPaint small = new() { IsAntialias = true, Typeface = Fonts.Inter.Medium, TextSize = 11f };
    readonly SKPaint fill = new() { IsAntialias = true };
    readonly SKPaint stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
    readonly SKPaint shaded = new() { IsAntialias = false };
    readonly SKPaint measure = new() { IsAntialias = true };
    readonly ShaderSlot glassSh = new(), lineSh = new(), bloomSh = new();
    readonly SKPath path = new();

    public static float HeightOf(DeskCard c) => c.Kind switch
    {
        "graph" => 300f,
        "watch" => HeadH + WatchRowH * Math.Min(4, c.Rows.Length) + 14f,
        "compare" => HeadH + 34f + RowH * Math.Min(8, c.Compare?.Length ?? 0) + 24f,
        _ => HeadH + RowH * Math.Min(10, c.Rows.Length) + (c.Foot.Length > 0 ? 30f : 14f),
    };

    public void Tick(bool wanted, double nowMs)
    {
        double dt = lastMs < 0 ? 16 : Math.Clamp(nowMs - lastMs, 0, 100);
        lastMs = nowMs;
        var c = DeskBoard.Card;
        bool on = DeskBoard.CardWanted;
        if (on && c != null && (shown == null || c.Id == shown.Id || k <= 0.002f)) shown = c;
        bool up = wanted && on && shown != null && c != null && c.Id == shown.Id;
        float target = up ? 1f : 0f;
        k += (target > k ? 1f : -1f) * (float)(dt / (target > k ? 420.0 : 320.0));
        k = Math.Clamp(k, 0f, 1f);
    }

    float Fit(string s, SKPaint p, float max, float size)
    {
        measure.Typeface = p.Typeface; measure.TextSize = size;
        float w = measure.MeasureText(s);
        return w > max ? Math.Max(size * 0.62f, size * max / w) : size;
    }

    public void Draw(SKCanvas cv, double nowMs)
    {
        var d = shown;
        if (k <= 0.002f || d == null) { LastRect = SKRect.Empty; return; }
        float e = k * k * (3f - 2f * k);
        float h = HeightOf(d);
        var r = new SKRect(Right - W, Top, Right, Top + h);
        LastRect = r;
        byte A(float x) => (byte)Math.Clamp(255f * x * e, 0f, 255f);
        int save = cv.Save();
        cv.Translate(r.Left + (1f - e) * 18f, r.Top);
        DeskLook.Glass(cv, W, h, e, nowMs, shaded, glassSh, lineSh, bloomSh);
        eyeP.Color = DeskLook.GoldHi.WithAlpha(A(0.95f));
        TextBlobs.DrawTracked(cv, d.Eyebrow.ToUpperInvariant(), 18f, 22f, 2f, eyeP);
        titleP.TextSize = Fit(d.Title.ToUpperInvariant(), titleP, W - 36f, 26f);
        titleP.Color = DeskLook.Ivory.WithAlpha(A(1f));
        TextBlobs.Draw(cv, d.Title.ToUpperInvariant(), 18f, 52f, titleP);
        float y = HeadH;
        if (d.Kind == "compare" && d.Compare != null)
        {
            // two names across the top, the rows between them
            rowC.Color = DeskLook.GoldHi.WithAlpha(A(1f));
            rowC.TextSize = Fit(d.HeadA, rowC, 150f, 19f); TextBlobs.Draw(cv, d.HeadA.ToUpperInvariant(), 100f, y + 20f, rowC);
            rowC.TextSize = Fit(d.HeadB, rowC, 150f, 19f); TextBlobs.Draw(cv, d.HeadB.ToUpperInvariant(), W - 100f, y + 20f, rowC);
            y += 34f;
            foreach (var (label, a, b) in d.Compare.Take(8))
            {
                fill.Color = new SKColor(255, 255, 255, A(0.05f));
                cv.DrawRect(new SKRect(12f, y + 2f, W - 12f, y + RowH - 2f), fill);
                rowC.TextSize = 19f; rowC.Color = DeskLook.Ivory.WithAlpha(A(1f));
                TextBlobs.Draw(cv, a, 100f, y + 23f, rowC);
                TextBlobs.Draw(cv, b, W - 100f, y + 23f, rowC);
                small.Color = DeskLook.Txt2.WithAlpha(A(0.85f)); small.TextAlign = SKTextAlign.Center;
                TextBlobs.Draw(cv, label.ToUpperInvariant(), W / 2f, y + 21f, small);
                small.TextAlign = SKTextAlign.Left;
                y += RowH;
            }
        }
        else if (d.Kind == "watch")
        {
            // 2026-11-06 (Drivers to watch): tag + class colour, name, the reason; the case-making number on the right
            foreach (var row in d.Rows.Take(4))
            {
                var acc = DeskLook.Hex(row.Accent, DeskLook.GoldMid);
                fill.Color = acc.WithAlpha(A(0.95f));
                cv.DrawRect(new SKRect(12f, y + 8f, 16f, y + WatchRowH - 8f), fill);
                eyeP.Color = DeskLook.GoldHi.WithAlpha(A(0.95f));
                TextBlobs.DrawTracked(cv, row.Pos.ToUpperInvariant(), 26f, y + 22f, 1.8f, eyeP);
                rowP.Color = DeskLook.Ivory.WithAlpha(A(1f));
                rowP.TextSize = Fit(row.Name.ToUpperInvariant(), rowP, W - 170f, 24f);
                TextBlobs.Draw(cv, row.Name.ToUpperInvariant(), 26f, y + 48f, rowP);
                small.Color = DeskLook.Txt2.WithAlpha(A(0.9f));
                small.TextSize = Fit(row.Sub, small, W - 44f, 12f);
                TextBlobs.Draw(cv, row.Sub, 26f, y + 68f, small);
                small.TextSize = 11f;
                rowR.TextSize = Fit(row.Value, rowR, 130f, 22f); rowR.Color = DeskLook.GoldHi.WithAlpha(A(1f));
                TextBlobs.Draw(cv, row.Value, W - 20f, y + 48f, rowR);
                rowR.TextSize = 19f;
                fill.Color = new SKColor(255, 255, 255, A(0.06f));
                cv.DrawRect(new SKRect(12f, y + WatchRowH - 1f, W - 12f, y + WatchRowH), fill);
                y += WatchRowH;
            }
        }
        else if (d.Kind == "graph" && d.Series is { Length: > 1 } s)
        {
            // gap over the last laps: 0 at the top (side by side), bigger gaps lower down
            var g = new SKRect(54f, y + 8f, W - 22f, h - 40f);
            float max = Math.Max(1f, (float)Math.Ceiling(s.Max()));
            fill.Color = new SKColor(255, 255, 255, A(0.06f));
            for (int i = 0; i <= 4; i++)
            {
                float gy = g.Top + g.Height * i / 4f;
                cv.DrawRect(new SKRect(g.Left, gy, g.Right, gy + 1f), fill);
                small.Color = DeskLook.Txt2.WithAlpha(A(0.7f)); small.TextAlign = SKTextAlign.Right;
                TextBlobs.Draw(cv, (max * i / 4f).ToString("0.0", CultureInfo.InvariantCulture) + "s", g.Left - 8f, gy + 4f, small);
            }
            small.TextAlign = SKTextAlign.Left;
            path.Reset();
            for (int i = 0; i < s.Length; i++)
            {
                float x = g.Left + g.Width * i / (s.Length - 1f), py = g.Top + g.Height * Math.Clamp(s[i] / max, 0f, 1f);
                if (i == 0) path.MoveTo(x, py); else path.LineTo(x, py);
            }
            stroke.Color = DeskLook.GoldHi.WithAlpha(A(1f));
            cv.DrawPath(path, stroke);
            float lx = g.Right, ly = g.Top + g.Height * Math.Clamp(s[^1] / max, 0f, 1f);
            fill.Color = DeskLook.GoldHi.WithAlpha(A(1f)); cv.DrawCircle(lx, ly, 4.5f, fill);
            small.Color = DeskLook.Txt2.WithAlpha(A(0.85f));
            TextBlobs.Draw(cv, $"LAST {s.Length} LAPS", g.Left, h - 16f, small);
            rowR.TextSize = 19f; rowR.Color = DeskLook.GoldHi.WithAlpha(A(1f));
            TextBlobs.Draw(cv, s[^1].ToString("0.000", CultureInfo.InvariantCulture) + "s", W - 22f, h - 14f, rowR);
        }
        else
        {
            foreach (var row in d.Rows.Take(10))
            {
                var acc = DeskLook.Hex(row.Accent, DeskLook.GoldMid);
                fill.Color = acc.WithAlpha(A(0.9f));
                cv.DrawRect(new SKRect(12f, y + 6f, 15f, y + RowH - 6f), fill);
                rowP.TextSize = 19f; rowP.Color = DeskLook.GoldHi.WithAlpha(A(1f));
                TextBlobs.Draw(cv, row.Pos, 24f, y + 24f, rowP);
                rowP.Color = DeskLook.Ivory.WithAlpha(A(1f));
                rowP.TextSize = Fit(row.Name.ToUpperInvariant(), rowP, W - 230f, 19f);
                TextBlobs.Draw(cv, row.Name.ToUpperInvariant(), 72f, y + 24f, rowP);
                rowR.TextSize = 19f; rowR.Color = DeskLook.Ivory.WithAlpha(A(0.95f));
                TextBlobs.Draw(cv, row.Value, W - 48f, y + 24f, rowR);
                if (row.Delta != 0)
                {
                    fill.Color = (row.Delta > 0 ? DeskLook.Up : DeskLook.Down).WithAlpha(A(1f));
                    path.Reset();
                    float ax = W - 30f, ay = y + RowH / 2f;
                    if (row.Delta > 0) { path.MoveTo(ax - 6, ay + 4); path.LineTo(ax + 6, ay + 4); path.LineTo(ax, ay - 5); }
                    else { path.MoveTo(ax - 6, ay - 4); path.LineTo(ax + 6, ay - 4); path.LineTo(ax, ay + 5); }
                    path.Close(); cv.DrawPath(path, fill);
                }
                fill.Color = new SKColor(255, 255, 255, A(0.06f));
                cv.DrawRect(new SKRect(12f, y + RowH - 1f, W - 12f, y + RowH), fill);
                y += RowH;
            }
            if (d.Foot.Length > 0) { small.Color = DeskLook.Txt2.WithAlpha(A(0.85f)); TextBlobs.Draw(cv, d.Foot, 18f, y + 20f, small); }
        }
        cv.RestoreToCount(save);
    }
}

/// <summary>The lower third (name tag) in the featured driver card's place, bottom centre.</summary>
public sealed class NameTagRenderer
{
    public const float W = 620f, H = 92f, Left = 650f, Top = 948f;
    float k; double lastMs = -1; NameTag? shown;
    public bool Active => k > 0.002f;
    public bool Fading => k > 0.002f && k < 0.998f;
    public SKRect LastRect { get; private set; }
    readonly SKPaint eyeP = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 10.5f };
    readonly SKPaint l1 = new() { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 32f };
    readonly SKPaint l2 = new() { IsAntialias = true, Typeface = Fonts.Inter.Medium, TextSize = 14f };
    readonly SKPaint fill = new() { IsAntialias = true };
    readonly SKPaint shaded = new() { IsAntialias = false };
    readonly SKPaint measure = new() { IsAntialias = true };
    readonly ShaderSlot glassSh = new(), lineSh = new(), bloomSh = new();

    public void Tick(bool wanted, double nowMs)
    {
        double dt = lastMs < 0 ? 16 : Math.Clamp(nowMs - lastMs, 0, 100);
        lastMs = nowMs;
        var t = DeskBoard.Tag;
        bool on = DeskBoard.TagWanted;
        if (on && t != null && (shown == null || t.Id == shown.Id || k <= 0.002f)) shown = t;
        bool up = wanted && on && shown != null && t != null && t.Id == shown.Id;
        float target = up ? 1f : 0f;
        k += (target > k ? 1f : -1f) * (float)(dt / (target > k ? 380.0 : 300.0));
        k = Math.Clamp(k, 0f, 1f);
        DeskBoard.TagUp = up || k > 0.002f;
    }

    public void Draw(SKCanvas c, double nowMs)
    {
        var t = shown;
        if (k <= 0.002f || t == null) { LastRect = SKRect.Empty; return; }
        float e = k * k * (3f - 2f * k);
        LastRect = new SKRect(Left, Top, Left + W, Top + H);
        byte A(float x) => (byte)Math.Clamp(255f * x * e, 0f, 255f);
        int save = c.Save();
        c.Translate(Left, Top + (1f - e) * 16f);
        // the bar wipes in from the left
        c.ClipRect(new SKRect(0, -4, W * Math.Clamp(e * 1.15f, 0f, 1f), H + 4));
        DeskLook.Glass(c, W, H, 1f, nowMs, shaded, glassSh, lineSh, bloomSh);
        var acc = DeskLook.Hex(t.Accent, DeskLook.GoldHi);
        fill.Color = acc.WithAlpha(A(1f));
        c.DrawRect(new SKRect(0, 0, 5f, H), fill);
        eyeP.Color = DeskLook.GoldHi.WithAlpha(A(0.95f));
        TextBlobs.DrawTracked(c, t.Eyebrow.ToUpperInvariant(), 22f, 24f, 2.2f, eyeP);
        measure.Typeface = l1.Typeface; measure.TextSize = 32f;
        string line1 = t.Line1.ToUpperInvariant();
        float w1 = measure.MeasureText(line1);
        l1.TextSize = w1 > W - 44f ? Math.Max(20f, 32f * (W - 44f) / w1) : 32f;
        l1.Color = DeskLook.Ivory.WithAlpha(A(1f));
        TextBlobs.Draw(c, line1, 22f, 58f, l1);
        l2.Color = DeskLook.Txt2.WithAlpha(A(0.92f));
        TextBlobs.Draw(c, t.Line2, 22f, 80f, l2);
        c.RestoreToCount(save);
    }
}
