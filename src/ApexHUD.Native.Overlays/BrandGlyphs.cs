using System.IO;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Corner names spelled in the brand's own lettering (2026-10-25, Angelo: "each word ... in that font only" and "make them
/// based off of the images"): the letters are cut out of the brand's logo picture, and the few a name needs that the
/// logo doesn't have are drawn to match (Porsche U and V, Michelin A). Kept per brand in
/// Documents\iRacing\Grandstand\corner_logos\glyphs &lt;brand&gt;\&lt;LETTER&gt;.png - Angelo's pictures, never shipped.
/// </summary>
public static class BrandGlyphs
{
    sealed record Style(float CapTop, float CapBottom, float Gap, float Space, float Shear);

    // where the capitals sit in each letter picture, the gap between letters and a space (both as a share of the cap
    // height), and the logo's slant
    static readonly Dictionary<string, Style> Styles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["porsche"] = new(0f, 1f, 13f / 76f, 60f / 76f, 0f),
        ["michelin"] = new(10f / 262f, 252f / 262f, 12f / 242f, 110f / 242f, -0.14f),
        // 2026-10-25: cut from Angelo's corvette type picture (upright; the slant is put back when drawn), N built from its strokes
        ["corvette"] = new(0f, 118f / 121f, 51f / 118f, 120f / 118f, -0.74f),
    };

    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "corner_logos");
    static readonly Dictionary<string, Dictionary<char, SKBitmap>?> banks = new(StringComparer.OrdinalIgnoreCase);
    static DateTime loadedAt = DateTime.MinValue;

    static Dictionary<char, SKBitmap>? Bank(string brand)
    {
        if ((DateTime.UtcNow - loadedAt).TotalSeconds > 60) { banks.Clear(); loadedAt = DateTime.UtcNow; }
        if (banks.TryGetValue(brand, out var b)) return b;
        Dictionary<char, SKBitmap>? d = null;
        try
        {
            var dir = Path.Combine(Root, "glyphs " + brand);
            if (Directory.Exists(dir))
            {
                d = new();
                foreach (var f in Directory.GetFiles(dir, "*.png"))
                {
                    var n = Path.GetFileNameWithoutExtension(f);
                    if (n.Length != 1) continue;
                    using var s = File.OpenRead(f);
                    if (Assets.Decode(s) is { } bmp) d[char.ToUpperInvariant(n[0])] = bmp;
                }
            }
        }
        catch { d = null; }
        return banks[brand] = d;
    }

    /// <summary>True when every letter of the text is in the brand's set.</summary>
    public static bool CanSpell(string brand, string text)
    {
        var b = Bank(brand);
        if (b == null || !Styles.ContainsKey(brand)) return false;
        foreach (var ch in text) if (ch != ' ' && !b.ContainsKey(char.ToUpperInvariant(ch))) return false;
        return true;
    }

    /// <summary>Width of the text at this cap height.</summary>
    public static float Measure(string brand, string text, float capH)
    {
        var b = Bank(brand)!; var st = Styles[brand];
        float w = 0; int n = 0;
        foreach (var ch in text)
        {
            if (n++ > 0) w += st.Gap * capH;
            if (ch == ' ') { w += st.Space * capH; continue; }
            var g = b[char.ToUpperInvariant(ch)];
            float sc = capH / ((st.CapBottom - st.CapTop) * g.Height);
            w += g.Width * sc;
        }
        return w + Math.Abs(st.Shear) * capH;
    }

    /// <summary>Draws the text with its left edge at x and the capitals sitting on baseY.</summary>
    public static void Draw(SKCanvas c, string brand, string text, float x, float baseY, float capH, float alpha, SKImageFilter? shadow = null)
    {
        var b = Bank(brand)!; var st = Styles[brand];
        using var p = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)Math.Clamp(255 * alpha, 0, 255)), ImageFilter = shadow };
        int sv = c.Save();
        if (st.Shear != 0) { c.Translate(x, baseY); c.Skew(st.Shear, 0); c.Translate(-x, -baseY); x += Math.Abs(st.Shear) * capH; }
        float cx = x; int n = 0;
        foreach (var ch in text)
        {
            if (n++ > 0) cx += st.Gap * capH;
            if (ch == ' ') { cx += st.Space * capH; continue; }
            var g = b[char.ToUpperInvariant(ch)];
            float sc = capH / ((st.CapBottom - st.CapTop) * g.Height);
            float gw = g.Width * sc, gh = g.Height * sc, top = baseY - capH - st.CapTop * gh;
            c.DrawBitmap(g, new SKRect(cx, top, cx + gw, top + gh), p);
            cx += gw;
        }
        c.RestoreToCount(sv);
    }
}
