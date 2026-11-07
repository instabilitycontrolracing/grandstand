using System.IO;
using SkiaSharp;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// 2026-11-05 (Angelo: "upload imagery for the cards"): a picture per graphic, chosen in the control app's card editor.
/// Files live in Documents\iRacing\Grandstand\card_images. Two looks:
///   glass - laid across the card behind the glass, ONLY where the card itself is drawn (SrcATop on the card's own
///           pixels), faint by default so text stays readable on stream
///   badge - a small logo in the card's top-right corner
/// Placement uses each graphic's design rectangle on a 1080p screen (the same boxes the Studio monitors show),
/// so it follows the card's position / size settings automatically.
/// </summary>
internal static class CardPictures
{
    public static readonly string Folder = Path.Combine(ControlState.Folder, "card_images");
    public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".webp" };

    // where each graphic sits on a 1920x1080 screen (x, y, w, h) - kept in step with ELEMENTS in control\app.js
    static readonly Dictionary<string, SKRect> Rects = new(StringComparer.Ordinal)
    {
        ["grid"] = R(410, 125, 1100, 830), ["spotlight"] = R(410, 125, 1100, 830), ["precards"] = R(560, 720, 800, 290),
        ["tower"] = R(16, 18, 400, 1046), ["splash"] = R(16, 18, 400, 1046), ["sessionbar"] = R(472, 18, 976, 92),
        ["featured"] = R(576, 853, 768, 211), ["battle"] = R(600, 762, 1000, 280), ["ticker"] = R(0, 110, 1920, 46),
        ["story"] = R(1010, 150, 880, 790), ["custom"] = R(1336, 150, 560, 252), ["fastest"] = R(600, 580, 720, 130),
        ["radio"] = R(1392, 560, 504, 196), ["viewer"] = R(472, 110, 976, 120), ["outro"] = R(0, 0, 1920, 1080),
        ["bets"] = R(1476, 104, 420, 128),   // 2026-11-06: the viewer-bet card (BetCardRenderer) - clickable in the Live view too
    };
    static SKRect R(float x, float y, float w, float h) => new(x, y, x + w, y + h);

    public static bool TryRect(string name, out SKRect r) => Rects.TryGetValue(name, out r);

    /// <summary>A safe file name inside the folder, or null.</summary>
    public static string? PathOf(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || file.Length > 120 || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || file.Contains("..")) return null;
        if (!Extensions.Contains(Path.GetExtension(file).ToLowerInvariant())) return null;
        return Path.Combine(Folder, file);
    }

    /// <summary>Decode a picture once (shrunk to at most 1600 px wide) - called when the setting changes, never per frame.</summary>
    public static SKImage? Load(string file)
    {
        try
        {
            var p = PathOf(file);
            if (p == null || !File.Exists(p)) return null;
            using var bmp = SKBitmap.Decode(p);
            if (bmp == null) return null;
            if (bmp.Width > 1600)
            {
                var info = new SKImageInfo(1600, Math.Max(1, (int)(bmp.Height * 1600f / bmp.Width)));
                using var small = bmp.Resize(info, SKFilterQuality.High);
                return small != null ? SKImage.FromBitmap(small) : SKImage.FromBitmap(bmp);
            }
            return SKImage.FromBitmap(bmp);
        }
        catch { return null; }
    }

    static readonly SKPaint glassPaint = new() { BlendMode = SKBlendMode.SrcATop, IsAntialias = true, FilterQuality = SKFilterQuality.High };
    static readonly SKPaint badgePaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.High };

    /// <summary>Start a layer before the graphic paints (glass look only). Returns the save count to restore to, or -1.</summary>
    public static int BeginGlass(SKCanvas c, SKRect r) => c.SaveLayer(Grow(r, 6f), null);

    /// <summary>After the graphic painted into the layer: lay the picture across its pixels, then close the layer.</summary>
    public static void EndGlass(SKCanvas c, SKImage img, SKRect r, float opacity, int save)
    {
        glassPaint.Color = new SKColor(255, 255, 255, (byte)(255 * Math.Clamp(opacity, 0f, 1f)));
        c.DrawImage(img, Cover(img, r), glassPaint);
        c.RestoreToCount(save);
    }

    public static void DrawBadge(SKCanvas c, SKImage img, SKRect r, float opacity)
    {
        float h = MathF.Min(56f, r.Height * 0.55f);
        float w = h * img.Width / Math.Max(1f, img.Height);
        float maxW = r.Width * 0.35f;
        if (w > maxW) { h *= maxW / w; w = maxW; }
        var dst = new SKRect(r.Right - w - 14f, r.Top + 10f, r.Right - 14f, r.Top + 10f + h);
        badgePaint.Color = new SKColor(255, 255, 255, (byte)(255 * Math.Clamp(opacity, 0f, 1f)));
        c.DrawImage(img, dst, badgePaint);
    }

    static SKRect Cover(SKImage img, SKRect r)
    {
        float s = MathF.Max(r.Width / img.Width, r.Height / img.Height);
        float w = img.Width * s, h = img.Height * s;
        return new SKRect(r.MidX - w / 2f, r.MidY - h / 2f, r.MidX + w / 2f, r.MidY + h / 2f);
    }

    static SKRect Grow(SKRect r, float d) => new(r.Left - d, r.Top - d, r.Right + d, r.Bottom + d);
}
