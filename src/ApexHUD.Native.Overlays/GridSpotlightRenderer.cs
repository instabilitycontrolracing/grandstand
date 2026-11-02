using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// The "hero" grid spotlight card (FEATURE_BACKLOG §2): a large, marque-forward
/// card spotlighting one driver, designed to cycle the front-runners of each
/// class before the field settles into the full STARTING GRID board. Reuses the
/// broadcast palette, bundled fonts, and manufacturer-logo set shared by the
/// rest of the suite, so it sits natively alongside the other overlays.
///
/// <para>This is the pure renderer (one card, given one driver). The pre-race
/// cycle/timing state machine and the live-overlay integration are layered on
/// top separately once the look is approved.</para>
/// </summary>
public sealed class GridSpotlightRenderer : IDisposable
{
    public const float CardWidth = 1040f;
    public const float CardHeight = 300f;

    static readonly SKColor Glass    = ParseHex(BroadcastPalette.TowerGlassBase, 232);
    static readonly SKColor Ivory    = ParseHex(BroadcastPalette.TextPrimary);
    static readonly SKColor Tertiary = ParseHex(BroadcastPalette.TextTertiary);
    static readonly SKColor Hairline = ParseHex(BroadcastPalette.BorderHairline, 210);
    static readonly SKColor Gold     = ParseHex(BroadcastPalette.Leader);

    readonly Dictionary<string, SKBitmap?> logoCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether to draw a placeholder nationality flag. Real flags
    /// (keyed off the driver's flair) swap in once a flag-image set exists.</summary>
    public bool ShowFlagPlaceholder { get; set; } = true;

    public bool Draw(SKCanvas c, float originX, float originY, Driver d)
    {
        var classColor = ParseHex(string.IsNullOrEmpty(d.ClassColor)
            ? BroadcastPalette.Class(d.ClassName) : d.ClassColor);
        bool isPole = d.PositionInClass == 1;

        c.Save();
        c.Translate(originX, originY);
        var card = new SKRect(0, 0, CardWidth, CardHeight);

        using (var glass = new SKPaint { IsAntialias = true, Color = Glass })
            c.DrawRoundRect(card, 16, 16, glass);
        using (var border = new SKPaint { IsAntialias = true, Color = Hairline, Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
            c.DrawRoundRect(card, 16, 16, border);
        using (var accent = new SKPaint { IsAntialias = true, Color = classColor })
            c.DrawRoundRect(new SKRect(12, 11, CardWidth - 12, 17), 3, 3, accent);

        // ---- left: position block ----
        using (var eb = new SKPaint { IsAntialias = true, Color = Gold, Typeface = Fonts.Inter.SemiBold, TextSize = 18 })
            TextTracked.Draw(c, eb, isPole ? "POLE" : "P" + d.PositionInClass, 60, 80, 3.4f);
        using (var pos = new SKPaint { IsAntialias = true, Color = isPole ? Gold : Ivory, Typeface = Fonts.CormorantGaramond.Bold, TextSize = 170, TextAlign = SKTextAlign.Center })
            BigText.Draw(c, d.PositionInClass > 0 ? d.PositionInClass.ToString() : "-", 132, 252, pos);   // drawn once, reused (4K perf)
        using (var div = new SKPaint { Color = Hairline, StrokeWidth = 1 })
            c.DrawLine(240, 54, 240, 252, div);

        // ---- identity: chips row ----
        float x = 272, cy = 58, h = 36;
        x = NumberChip(c, x, cy, h, string.IsNullOrEmpty(d.CarNumber) ? "-" : d.CarNumber) + 10;
        string lic = string.IsNullOrEmpty(d.LicenceString) ? "R" : d.LicenceString[..1].ToUpperInvariant();
        x = LicenseChip(c, x, cy, h, lic) + 10;
        x = ClassChip(c, x, cy, h, d.ClassName, classColor) + 12;
        if (ShowFlagPlaceholder) FlagPlaceholder(c, x, cy + 4, h - 8);

        // ---- name / car / qualified ----
        using (var nm = new SKPaint { IsAntialias = true, Color = Ivory, Typeface = Fonts.CormorantGaramond.Bold, TextSize = 58 })
            c.DrawText(string.IsNullOrEmpty(d.Name) ? d.AbbrevName : d.Name, 272, 178, nm);
        if (!string.IsNullOrEmpty(d.CarName))
            using (var cm = new SKPaint { IsAntialias = true, Color = Tertiary, Typeface = Fonts.Inter.Medium, TextSize = 17 })
                TextTracked.Draw(c, cm, d.CarName.ToUpperInvariant(), 274, 210, 2f);
        if (d.BestLapTime > 0)
            using (var q = new SKPaint { IsAntialias = true, Color = Gold, Typeface = Fonts.Inter.SemiBold, TextSize = 15 })
                TextTracked.Draw(c, q, "QUALIFIED   " + LapTimeFormat.Lap(d.BestLapTime), 274, 238, 1.2f);

        // ---- right: marque logo ----
        var logo = LoadLogo(d.CarBrand);
        if (logo != null)
        {
            float maxW = 168, maxH = 156;
            float s = Math.Min(maxW / logo.Width, maxH / logo.Height);
            float dw = logo.Width * s, dh = logo.Height * s;
            float lcx = CardWidth - 56 - maxW / 2f, lcy = CardHeight / 2f + 2;
            var dst = new SKRect(lcx - dw / 2f, lcy - dh / 2f, lcx + dw / 2f, lcy + dh / 2f);
            using var lp = new SKPaint
            {
                IsAntialias = true,
                FilterQuality = SKFilterQuality.High,
                ColorFilter = SKColorFilter.CreateBlendMode(new SKColor(255, 255, 255, 252), SKBlendMode.DstIn),
            };
            c.DrawBitmap(logo, dst, lp);
        }

        c.Restore();
        return true;
    }

    float NumberChip(SKCanvas c, float x, float y, float h, string num)
    {
        float w = 64;
        using var bg = new SKPaint { IsAntialias = true, Color = SKColors.White };
        c.DrawRoundRect(new SKRect(x, y, x + w, y + h), 5, 5, bg);
        using var t = new SKPaint { IsAntialias = true, Color = new SKColor(8, 10, 16), Typeface = Fonts.Inter.Bold, TextSize = 22, TextAlign = SKTextAlign.Center };
        t.GetFontMetrics(out var m);
        c.DrawText(num, x + w / 2f, y + h / 2f - (m.Ascent + m.Descent) / 2f, t);
        return x + w;
    }

    float LicenseChip(SKCanvas c, float x, float y, float h, string lic)
    {
        float w = 40;
        using var bg = new SKPaint { IsAntialias = true, Color = ParseHex(BroadcastPalette.License(lic)) };
        c.DrawRoundRect(new SKRect(x, y, x + w, y + h), 5, 5, bg);
        using var t = new SKPaint { IsAntialias = true, Color = ParseHex(BroadcastPalette.LicenseText(lic)), Typeface = Fonts.Inter.Bold, TextSize = 20, TextAlign = SKTextAlign.Center };
        t.GetFontMetrics(out var m);
        c.DrawText(lic, x + w / 2f, y + h / 2f - (m.Ascent + m.Descent) / 2f, t);
        return x + w;
    }

    float ClassChip(SKCanvas c, float x, float y, float h, string className, SKColor classColor)
    {
        if (string.IsNullOrEmpty(className)) return x;
        using var label = new SKPaint { IsAntialias = true, Color = classColor, Typeface = Fonts.Inter.Bold, TextSize = 16 };
        var labelText = className.ToUpperInvariant();
        float labelW = label.MeasureText(labelText) + 1.2f * Math.Max(0, labelText.Length - 1);
        float w = labelW + 30;
        using (var border = new SKPaint { IsAntialias = true, Color = classColor.WithAlpha(150), Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
            c.DrawRoundRect(new SKRect(x, y, x + w, y + h), 5, 5, border);
        using (var stripe = new SKPaint { IsAntialias = true, Color = classColor })
            c.DrawRect(new SKRect(x + 9, y + 9, x + 12, y + h - 9), stripe);
        label.GetFontMetrics(out var m);
        TextTracked.Draw(c, label, className.ToUpperInvariant(), x + 18, y + h / 2f - (m.Ascent + m.Descent) / 2f, 1.2f);
        return x + w;
    }

    void FlagPlaceholder(SKCanvas c, float x, float y, float h)
    {
        float w = h * 1.5f;
        var cols = new[] { new SKColor(60, 90, 150), new SKColor(225, 228, 232), new SKColor(170, 60, 70) };
        for (int i = 0; i < 3; i++)
            using (var p = new SKPaint { IsAntialias = true, Color = cols[i] })
                c.DrawRect(new SKRect(x + i * w / 3f, y, x + (i + 1) * w / 3f, y + h), p);
        using var b = new SKPaint { IsAntialias = true, Color = Hairline, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        c.DrawRect(new SKRect(x, y, x + w, y + h), b);
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

    static SKColor ParseHex(string hex, byte alpha = 255)
    {
        var s = hex.AsSpan();
        if (s.Length > 0 && s[0] == '#') s = s[1..];
        if (s.Length != 6) return new SKColor(255, 255, 255, alpha);
        byte r = byte.Parse(s[..2], System.Globalization.NumberStyles.HexNumber);
        byte g = byte.Parse(s[2..4], System.Globalization.NumberStyles.HexNumber);
        byte b = byte.Parse(s[4..6], System.Globalization.NumberStyles.HexNumber);
        return new SKColor(r, g, b, alpha);
    }

    public void Dispose()
    {
        foreach (var b in logoCache.Values) b?.Dispose();
        logoCache.Clear();
    }
}
