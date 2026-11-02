using System.IO;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Loads files from the bundled <c>assets/</c> folder that the build copies
/// next to the executable. Centralising the path convention here means every
/// overlay resolves images and fonts the same way. This is the reusable
/// asset-loading pattern established in Port 01.
/// </summary>
public static class Assets
{
    static readonly string Root = Path.Combine(AppContext.BaseDirectory, "assets");

    /// <summary>Absolute path to a file under the bundled assets folder.</summary>
    public static string Resolve(params string[] segments)
    {
        var all = new string[segments.Length + 1];
        all[0] = Root;
        segments.CopyTo(all, 1);
        return Path.Combine(all);
    }

    /// <summary>Decode a bundled image (e.g. <c>LoadBitmap("clock", "dial.png")</c>).</summary>
    public static SKBitmap LoadBitmap(params string[] segments)
    {
        var path = Resolve(segments);
        using var stream = File.OpenRead(path);
        var bmp = SKBitmap.Decode(stream)
            ?? throw new InvalidOperationException($"Could not decode bitmap: {path}");
        // 2026-10-24 (4K perf): an immutable bitmap is uploaded to the graphics card ONCE and reused;
        // a mutable one can be re-uploaded (and re-mipmapped at High quality) on every draw.
        bmp.SetImmutable();
        return bmp;
    }

    /// <summary>Decode a logo/photo for drawing: like SKBitmap.Decode, but immutable, so the GPU
    /// uploads it once instead of on every frame (2026-10-24 4K perf). Not for reused frame buffers.</summary>
    public static SKBitmap? Decode(Stream stream) { var b = SKBitmap.Decode(stream); b?.SetImmutable(); return b; }
    public static SKBitmap? Decode(string path) { var b = SKBitmap.Decode(path); b?.SetImmutable(); return b; }

    /// <summary>
    /// Load a bundled font from <c>assets/fonts/</c> into a SkiaSharp typeface.
    /// Accepts a flat file name (<c>LoadTypeface("RaycerRay-…ttf")</c>) or a
    /// sub-foldered path (<c>LoadTypeface("inter", "Inter-Regular.ttf")</c>).
    /// </summary>
    public static SKTypeface LoadTypeface(params string[] segments)
    {
        var under = new string[segments.Length + 1];
        under[0] = "fonts";
        segments.CopyTo(under, 1);
        var path = Resolve(under);
        SKTypeface? tf = null;
        try { if (File.Exists(path)) tf = SKTypeface.FromFile(path); } catch { tf = null; }
        return tf ?? FallbackTypeface(path);
    }

    /// <summary>2026-10-28 (distribution): a missing or broken bundled font must not crash the overlay (the first text
    /// draw used to throw). Falls back to the same family installed on the PC, then to a close system font, at the
    /// weight the file name asks for (Regular/Medium/SemiBold/Bold/ExtraBold/Black). Logged once per file.</summary>
    static SKTypeface FallbackTypeface(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        string fam = name.Split('-')[0];
        string w = name.Contains('-') ? name[(name.IndexOf('-') + 1)..] : "Regular";
        var weight = w switch
        {
            "Medium" => SKFontStyleWeight.Medium, "SemiBold" => SKFontStyleWeight.SemiBold, "Bold" => SKFontStyleWeight.Bold,
            "ExtraBold" => SKFontStyleWeight.ExtraBold, "Black" => SKFontStyleWeight.Black, _ => SKFontStyleWeight.Normal,
        };
        // "BarlowCondensed" -> "Barlow Condensed", "CormorantGaramond" -> "Cormorant Garamond"
        string spaced = System.Text.RegularExpressions.Regex.Replace(fam, "(?<=[a-z])(?=[A-Z])", " ");
        bool condensed = fam.Contains("Condensed", StringComparison.OrdinalIgnoreCase);
        bool serif = fam.Contains("Garamond", StringComparison.OrdinalIgnoreCase);
        var style = new SKFontStyle(weight, condensed ? SKFontStyleWidth.Condensed : SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        string[] tries = condensed ? new[] { spaced, "Bahnschrift Condensed", "Arial Narrow", "Segoe UI", "DejaVu Sans Condensed" }
                       : serif ? new[] { spaced, "Garamond", "Georgia", "Times New Roman", "DejaVu Serif" }
                       : new[] { spaced, "Segoe UI", "Arial", "DejaVu Sans" };
        SKTypeface? tf = null;
        foreach (var f in tries)
        {
            try { tf = SKFontManager.Default.MatchFamily(f, style); } catch { tf = null; }
            if (tf != null) break;
        }
        tf ??= SKTypeface.FromFamilyName(null, style) ?? SKTypeface.Default;
        try { OverlayLog.Write($"font missing: {path} - using {tf.FamilyName} instead (put the bundled fonts back in assets\\fonts)"); } catch { }
        return tf;
    }
}
