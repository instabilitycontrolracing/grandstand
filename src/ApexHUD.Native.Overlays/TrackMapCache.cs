using System.IO;
using System.Text.Json;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Brings recorded track maps (Documents\iRacing\ApexHUD\maps) into assets\maps, rendering
/// each one's PNG (white line on transparent + start/finish tick, the same look as the Le Mans
/// map). Cheap to call every frame: it only does work when the store's version changes.
/// </summary>
public static class TrackMapCache
{
    static int synced = -1;
    static readonly object gate = new();

    /// <summary>Changes whenever new maps were brought in - lookups rebuild their index on change.</summary>
    public static int Generation { get; private set; }

    public static void Sync()
    {
        int v = TrackMapStore.Version;
        if (v == synced) return;
        lock (gate)
        {
            if (v == synced) return;
            synced = v;
            try
            {
                if (!Directory.Exists(TrackMapStore.Folder)) return;
                var dest = Assets.Resolve("maps");
                Directory.CreateDirectory(dest);
                bool any = false;
                foreach (var json in Directory.GetFiles(TrackMapStore.Folder, "*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(json);
                    string dj = Path.Combine(dest, name + ".json"), dp = Path.Combine(dest, name + ".png");
                    if (File.Exists(dj) && File.GetLastWriteTimeUtc(dj) >= File.GetLastWriteTimeUtc(json)) continue;
                    File.Copy(json, dj, true);
                    if (File.Exists(dp)) File.Delete(dp);   // re-drawn on demand from the new line
                    any = true;
                }
                if (any) Generation++;
            }
            catch { /* a map is a nicety - never break the overlay */ }
        }
    }

    /// <summary>
    /// The map for this track + layout (file name without extension) in assets\maps, with its PNG
    /// drawn on first use. Remembered per track, so the folder isn't scanned every frame.
    /// </summary>
    public static string? Resolve(string? config, string? name1, string? name2, string? name3)
    {
        // asked every frame by the opener and the grid board (2026-10-24): same track + layout as last time ->
        // the remembered answer, without building the lookup key (or a params array) each frame
        Sync();
        lock (gate)
        {
            if (lastGen == Generation && lastConfig == config && lastN1 == name1 && lastN2 == name2 && lastN3 == name3) return lastHit;
        }
        var hit = Resolve(config, new[] { name1, name2, name3 });
        lock (gate) { lastGen = Generation; lastConfig = config; lastN1 = name1; lastN2 = name2; lastN3 = name3; lastHit = hit; }
        return hit;
    }
    static int lastGen = -1; static string? lastConfig, lastN1, lastN2, lastN3, lastHit;

    public static string? Resolve(string? config, params string?[] names)
    {
        Sync();
        string key = Generation + "|" + config + "|" + string.Join("|", names);
        lock (gate)
        {
            if (memo.TryGetValue(key, out var hit)) return hit;
            string? b = null;
            try
            {
                var dir = Assets.Resolve("maps");
                b = TrackMapStore.FindBaseName(dir, config, names);
                if (b != null)
                {
                    string png = Path.Combine(dir, b + ".png"), json = Path.Combine(dir, b + ".json");
                    if (!File.Exists(png) && File.Exists(json)) RenderPng(json, png);
                }
            }
            catch { b = null; }
            memo[key] = b;
            return b;
        }
    }
    static readonly Dictionary<string, string?> memo = new();

    static void RenderPng(string jsonPath, string pngPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        int w = doc.RootElement.GetProperty("w").GetInt32(), h = doc.RootElement.GetProperty("h").GetInt32();
        var pts = doc.RootElement.GetProperty("points").EnumerateArray()
                     .Select(p => new SKPoint(p[0].GetSingle(), p[1].GetSingle())).ToList();
        if (pts.Count < 3) return;
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.Transparent);
        using var path = new SKPath();
        path.MoveTo(pts[0]);
        foreach (var p in pts.Skip(1)) path.LineTo(p);
        path.Close();
        using (var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 11, StrokeJoin = SKStrokeJoin.Round,
                                        StrokeCap = SKStrokeCap.Round, Color = SKColors.White })
            c.DrawPath(path, line);
        // start/finish tick across the line at the first point
        var a0 = pts[0]; var a1 = pts[Math.Min(5, pts.Count - 1)];
        float dx = a1.X - a0.X, dy = a1.Y - a0.Y, len = MathF.Max(1e-3f, MathF.Sqrt(dx * dx + dy * dy));
        float nx = -dy / len, ny = dx / len;
        using (var tick = new SKPaint { IsAntialias = true, StrokeWidth = 6, Color = SKColors.White })
            c.DrawLine(a0.X - nx * 22, a0.Y - ny * 22, a0.X + nx * 22, a0.Y + ny * 22, tick);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(pngPath);
        data.SaveTo(fs);
    }
}
