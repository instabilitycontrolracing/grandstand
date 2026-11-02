using System.IO;
using ApexHUD.Native.Common;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// A logo for a famous corner (2026-10-25, Angelo: "the famous corners that have branding like the Porsche Curves we
/// should show the Porsche text logo, or for Ford Chicane show a Ford emblem, or for PlayStation corner a PlayStation
/// logo ... do that for every track that has iconic names. If it's a driver's name maybe a portrait of that driver").
///
/// 1. Angelo's own pictures first: Documents\iRacing\Grandstand\corner_logos\*.png. The file name is the word to look
///    for in the corner name ("playstation.png" -> Playstation Chicane, "michelin.png", "dunlop.png", "senna.png" for a
///    driver portrait...). Personal pictures: they stay on his PC and are never shipped.
/// 2. Otherwise a car maker named in the corner (Porsche Curves, Ford Chicanes, Corvette Corner, Mercedes Arena...) gets
///    the maker logo Grandstand already has.
/// </summary>
public static class CornerBrand
{
    static readonly string UserDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "corner_logos");
    static readonly Dictionary<string, SKBitmap?> cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, SKBitmap?> files = new(StringComparer.OrdinalIgnoreCase);
    static DateTime scannedAt = DateTime.MinValue;
    static (string Word, string Path)[] userWords = Array.Empty<(string, string)>();
    static long userStamp;
    static readonly object gate = new();
    static Task? scanning;

    static readonly string[] Makers = { "Porsche", "Ford", "Corvette", "Ferrari", "Mercedes", "Audi", "BMW", "Honda", "Toyota", "Nissan",
                                        "Mazda", "Lexus", "Cadillac", "Chevrolet", "Bentley", "Jaguar", "Aston", "Lamborghini", "Mclaren", "Hyundai" };

    /// <summary>One of Angelo's own pictures by its file name (e.g. "michelin"), or null when he hasn't got it.</summary>
    public static SKBitmap? User(string word)
    {
        Rescan();
        var words = userWords;
        foreach (var (w, p) in words) if (w.Equals(word, StringComparison.OrdinalIgnoreCase)) return Load(p);
        return null;
    }

    /// <summary>The logo for this corner or straight, or null.</summary>
    public static SKBitmap? For(string? place)
    {
        if (string.IsNullOrWhiteSpace(place)) return null;
        Rescan();
        lock (gate) if (cache.TryGetValue(place, out var hit)) return hit;
        SKBitmap? bmp = null;
        foreach (var (w, p) in userWords)
            if (Regex.IsMatch(place, $@"\b{Regex.Escape(w)}\b", RegexOptions.IgnoreCase)) { bmp = Load(p); if (bmp != null) break; }
        if (bmp == null)
            foreach (var m in Makers)
                if (Regex.IsMatch(place, $@"\b{m}\b", RegexOptions.IgnoreCase))
                {
                    var slug = ManufacturerBrand.Resolve(m);
                    if (slug != null) bmp = Load(Assets.Resolve("logos", "manufacturers", slug + ".png"));
                    if (bmp != null) break;
                }
        lock (gate) return cache[place] = bmp;
    }

    /// <summary>#505 (2026-10-30, Angelo: "when the Michelin pit stop card comes on the animation is very choppy"): the
    /// folder look (every 30 s: the file list + each file's write time) and the decoding of his pictures ran on the render
    /// thread - in the middle of whatever card asked first, the Michelin pit card included. Now they run in the background
    /// and the finished set is swapped in; the render thread only ever reads. The very first look waits for its result
    /// once (at start-up), so nothing that asks early gets a wrong "no picture".</summary>
    static void Rescan()
    {
        if ((DateTime.UtcNow - scannedAt).TotalSeconds < 30) return;
        Task? first = null;
        lock (gate)
        {
            if ((DateTime.UtcNow - scannedAt).TotalSeconds < 30) return;
            bool initial = scannedAt == DateTime.MinValue;
            scannedAt = DateTime.UtcNow;
            if (scanning == null || scanning.IsCompleted) scanning = Task.Run(ScanWork);
            if (initial) first = scanning;
        }
        try { first?.Wait(5000); } catch { }
    }

    static void ScanWork()
    {
        try
        {
            var list = Directory.Exists(UserDir)
                ? Directory.GetFiles(UserDir, "*.png").Select(f => (Path.GetFileNameWithoutExtension(f).Replace('_', ' ').Trim(), f)).Where(x => x.Item1.Length > 1).ToArray()
                : Array.Empty<(string, string)>();
            // a picture replaced under the same name is picked up too (its write time is part of the check)
            long stamp = list.Aggregate(17L, (h, x) => h * 31 + File.GetLastWriteTimeUtc(x.Item2).Ticks);
            if (list.Length == userWords.Length && list.SequenceEqual(userWords) && stamp == userStamp) return;
            // decode the whole set here, off the render thread, then swap it in
            var fresh = new Dictionary<string, SKBitmap?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, path) in list) fresh[path] = Decode(path);
            lock (gate)
            {
                foreach (var k in files.Keys.Where(k => k.StartsWith(UserDir, StringComparison.OrdinalIgnoreCase)).ToList()) files.Remove(k);
                foreach (var kv in fresh) files[kv.Key] = kv.Value;
                cache.Clear();
                userWords = list; userStamp = stamp;
            }
        }
        catch { }
    }

    static SKBitmap? Decode(string path)
    {
        try { if (File.Exists(path)) { using var s = File.OpenRead(path); return Assets.Decode(s); } } catch { }
        return null;
    }

    static SKBitmap? Load(string path)
    {
        lock (gate) if (files.TryGetValue(path, out var b)) return b;
        var d = Decode(path);       // a maker logo from the app's own assets (the user pictures are decoded by the scan)
        lock (gate) return files[path] = d;
    }

    /// <summary>Draws the logo fitted into a box (keeps its shape), centred vertically; returns the width used.</summary>
    public static float Draw(SKCanvas c, SKBitmap logo, float x, float midY, float maxW, float maxH, float alpha, bool alignRight = false)
    {
        float s = Math.Min(maxW / logo.Width, maxH / logo.Height);
        float w = logo.Width * s, h = logo.Height * s;
        float left = alignRight ? x - w : x;
        using var p = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)Math.Clamp(255 * alpha, 0, 255)) };
        c.DrawBitmap(logo, new SKRect(left, midY - h / 2, left + w, midY + h / 2), p);
        return w;
    }
}
