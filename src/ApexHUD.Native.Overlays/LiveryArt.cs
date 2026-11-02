using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Real car pictures on the broadcast (2026-10-25, Angelo: "show the real car liveries on broadcast graphics";
/// then "it's getting really close ... maybe render the cars in some kind of renderer, like a Trading Paints paint
/// booth"). The first version cut the side panel out of each Trading Paints file - a car's paint "peeled flat" - and
/// it never looked like a car (warped, no wheels, no glass). This version uses iRacing's OWN car renderer: the
/// iRacing service on the PC (iRacingService64, the same one the iRacing app uses for its car pictures) draws any car
/// with any paint at http://127.0.0.1:32034/pk_car.png - a real 3D three-quarter view, transparent background,
/// 508x272. We ask it for each driver's car with their Trading Paints file (car_&lt;id&gt;.tga / car_team_&lt;id&gt;.tga),
/// or with their iRacing paint scheme (pattern, colours, number style from the session info) when they have none.
///
/// Pictures are saved to Documents\iRacing\Grandstand\livery\cars, so they show instantly next time and still work
/// when the service is busy. A new paint file (Trading Paints downloading mid-session) means a new picture.
/// Everything is on a background thread; <see cref="Get"/> returns null until the picture is ready and never blocks
/// a frame.
/// </summary>
public static class LiveryArt
{
    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing");
    static readonly string CacheDir = Path.Combine(Root, "Grandstand", "livery", "cars");
    static readonly string PaintDir = Path.Combine(Root, "paint");
    const string Service = "http://127.0.0.1:32034/pk_car.png";
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };

    sealed class Entry { public SKImage? Image; public string Id = ""; public bool Loading; public double TriedAtMs = -1e9, CheckedAtMs = -1e9; }
    static readonly ConcurrentDictionary<string, Entry> cache = new();
    static readonly SemaphoreSlim gate = new(1, 1);       // one picture at a time

    /// <summary>The driver's car (three-quarter view, transparent, trimmed to the car), or null while loading / unavailable.</summary>
    public static SKImage? Get(Driver? d)
    {
        if (d == null || string.IsNullOrEmpty(d.CarPath) || d.IsPaceCar || d.IsSpectator) return null;
        var e = cache.GetOrAdd(d.CarIdx + ":" + d.CarPath + ":" + d.UserId + ":" + d.TeamId, _ => new Entry());
        double now = Environment.TickCount64;
        // look again every 30 s (a Trading Paints file may arrive or change); retry a failure every 20 s
        bool due = e.Image == null ? now - e.TriedAtMs > 20000 : now - e.CheckedAtMs > 30000;
        if (due && !e.Loading)
        {
            e.Loading = true; e.TriedAtMs = e.CheckedAtMs = now;
            Task.Run(() => Load(d, e));
        }
        return e.Image;
    }

    // ---- 2026-10-25: real-lighting FRONT photos from the photo booth (C:\SimRacing\Tools\photo_booth.py, run on a
    // replay's formation lap): Documents\iRacing\Grandstand\livery\photos\<car>_<user>_<team>_<paint stamp>_front.png
    static readonly string PhotoDir = Path.Combine(Root, "Grandstand", "livery", "photos");
    sealed class Front { public SKImage? Image; public string File = ""; public bool Loading; public double CheckedAtMs = -1e9; }
    static readonly ConcurrentDictionary<string, Front> fronts = new();
    const bool PhotoBoothEnabled = false;

    /// <summary>The driver's car photographed from the front in the sim (real lighting, background cut away), or null
    /// when the photo booth hasn't shot it yet. For the big pictures (Your Race card).</summary>
    public static SKImage? GetFront(Driver? d)
    {
        // 2026-10-27 (Angelo: "the thing that takes pictures of the fronts of the cars ... does not work in reality - just
        // display what iRacing gives us as the Trading Paints paint scheme"): the photo booth is retired, every card uses
        // the livery picture (Get) instead
        if (!PhotoBoothEnabled) return null;
        if (d == null || string.IsNullOrEmpty(d.CarPath) || d.IsPaceCar) return null;
        var f = fronts.GetOrAdd($"{d.CarPath}_{d.UserId}_{d.TeamId}_", _ => new Front());
        double now = Environment.TickCount64;
        if (!f.Loading && now - f.CheckedAtMs > 15000)
        {
            f.Loading = true; f.CheckedAtMs = now;
            string prefix = $"{d.CarPath}_{d.UserId}_{d.TeamId}_";
            Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(PhotoDir)) return;
                    var newest = new DirectoryInfo(PhotoDir).GetFiles(prefix + "*_front.png").OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
                    if (newest == null || newest.FullName + newest.LastWriteTimeUtc.Ticks == f.File) return;
                    var img = Trim(File.ReadAllBytes(newest.FullName), photo: true);
                    if (img == null) return;
                    var old = f.Image; f.Image = img; f.File = newest.FullName + newest.LastWriteTimeUtc.Ticks; old?.Dispose();
                    OverlayLog.Write($"[livery] #{d.CarNumber} front photo -> {img.Width}x{img.Height}");
                }
                catch (Exception ex) { OverlayLog.Write($"[livery] front #{d.CarNumber}: {ex.Message}"); }
                finally { f.Loading = false; }
            });
        }
        return f.Image;
    }

    static double lastWarm = -1e9;
    /// <summary>Every 5 s: make sure every car in the field has its picture queued, so cards never wait for one.</summary>
    public static void Prewarm(RaceSnapshot snap)
    {
        double now = Environment.TickCount64;
        if (now - lastWarm < 5000 || !snap.IsConnected) return;
        lastWarm = now;
        foreach (var cl in snap.ClassLeaderboard)
            foreach (var d in cl.Drivers) Get(d);
    }

    /// <summary>Test hook (preview tool): use this picture for the driver instead of asking the renderer.</summary>
    public static void Inject(Driver d, byte[] png)
    {
        var e = cache.GetOrAdd(d.CarIdx + ":" + d.CarPath + ":" + d.UserId + ":" + d.TeamId, _ => new Entry());
        e.Image = Trim(png); e.Id = "test"; e.CheckedAtMs = e.TriedAtMs = 1e18;
    }

    /// <summary>The driver's paint file. A "car_num_" file is a paint with its own race numbers painted on (Trading
    /// Paints' custom-number paints): iRacing draws no number over those, in the sim or in the picture (2026-10-25,
    /// Angelo: liveries carry their own numbers and logos - don't stamp over them).</summary>
    static (string File, bool OwnNumber)? PaintFile(Driver d)
    {
        string dir = Path.Combine(PaintDir, d.CarPath);
        var names = new List<(string, bool)>();
        if (d.TeamId > 0) { names.Add(($"car_num_team_{d.TeamId}.tga", true)); names.Add(($"car_team_{d.TeamId}.tga", false)); }
        if (d.UserId > 0) { names.Add(($"car_num_{d.UserId}.tga", true)); names.Add(($"car_{d.UserId}.tga", false)); }
        foreach (var (name, own) in names)
            if (File.Exists(Path.Combine(dir, name))) return (Path.Combine(dir, name), own);
        return null;
    }

    static string Q(string s) => Uri.EscapeDataString(s);

    /// <summary>The renderer's address for this driver (same parameters the iRacing app sends).</summary>
    static string Url(Driver d, string? paint, bool ownNumber)
    {
        var car = (d.CarDesign ?? "").Split(',');
        string carPat = car.Length > 0 && car[0].Length > 0 ? car[0] : "1";
        string carCol = car.Length >= 4 ? string.Join(",", car[1], car[2], car[3]) : "ffffff,ffffff,001c81";
        var num = (d.NumberDesign ?? "").Split(',');
        string numPat = num.Length > 0 && num[0].Length > 0 ? num[0] : "0";
        string numSl = num.Length > 1 && num[1].Length > 0 ? num[1] : "0";
        string numCol = num.Length >= 5 ? string.Join(",", num[2], num[3], num[4]) : "ffffff,777777,000000";
        string rimCol = car.Length >= 5 && car[4].Length > 0 ? Q(car[4]) : "null";
        // a blank number = the renderer stamps none (the paint's own numbers show, as in the sim)
        string number = ownNumber ? "" : string.IsNullOrEmpty(d.CarNumber) ? "0" : d.CarNumber;
        return $"{Service}?size=2&carPath={Q(d.CarPath)}&noDecal=false&carCustPaint={(paint == null ? "null" : Q(paint))}" +
               $"&carPat={Q(carPat)}&carCol={Q(carCol)}&licCol=&sponsors={Q(string.IsNullOrEmpty(d.Sponsors) ? "0,0" : d.Sponsors)}" +
               $"&noNum=false&number={Q(number)}&numSlnt={Q(numSl)}&numPat={Q(numPat)}&numCol={Q(numCol)}" +
               $"&carRimType=null&carRimCol={rimCol}&carCfg=-1&carCfgSubDir=&carCfgCustomPaintExt=";
    }

    static async Task Load(Driver d, Entry e)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var pf = PaintFile(d);
            string? paint = pf?.File;
            string stamp = paint == null ? "ir" : File.GetLastWriteTimeUtc(paint).Ticks.ToString("x");
            string url = Url(d, paint, pf?.OwnNumber == true);
            // the picture's identity: the car, the paint file version and the scheme / number it was drawn with
            string id = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(url + "|" + stamp)))[..12];
            if (e.Image != null && e.Id == id) return;
            string file = Path.Combine(CacheDir, $"{d.CarPath}_{d.CarNumber}_{id}.png".Replace('\\', '_'));
            byte[]? png = null;
            if (File.Exists(file)) png = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
            else
            {
                try { png = await http.GetByteArrayAsync(url).ConfigureAwait(false); }
                catch (Exception ex) { OverlayLog.Write($"[livery] #{d.CarNumber} {d.CarPath}: iRacing renderer not answering ({ex.GetType().Name})"); }
                if (png is { Length: > 100 })
                {
                    Directory.CreateDirectory(CacheDir);
                    await File.WriteAllBytesAsync(file, png).ConfigureAwait(false);
                }
            }
            if (png is not { Length: > 100 }) return;
            var img = Trim(png);
            if (img == null) return;
            var old = e.Image;
            e.Image = img; e.Id = id;
            old?.Dispose();
            OverlayLog.Write($"[livery] #{d.CarNumber} {d.CarPath}: {(paint != null ? Path.GetFileName(paint) : "iRacing paint")} -> {img.Width}x{img.Height}");
        }
        catch (Exception ex) { OverlayLog.Write($"[livery] #{d.CarNumber}: {ex.Message}"); }
        finally { e.Loading = false; gate.Release(); }
    }

    /// <summary>
    /// A more flattering look (2026-10-25, Angelo: "more flattering lighting or contrast"). iRacing's renderer lights the
    /// cars flat and a little grey; this adds contrast (x1.2) and colour (x1.18), a touch of brightness, and light from
    /// above: up to +18 % at the roof, shading down to -18 % at the tyres - like a studio key light. Picked from four
    /// looks compared side by side ("top light"); the "punchy" one blew out white liveries.
    /// </summary>
    static void Grade(SKBitmap bmp, double Bright = 1.02, double Contrast = 1.20, double Colour = 1.18, double Sheen = 0.6)
    {
        int w = bmp.Width, h = bmp.Height;
        var px = bmp.Bytes;    // RGBA, unpremultiplied as decoded
        double sum = 0;
        for (int i = 0; i < px.Length; i += 4) sum += 0.299 * px[i] + 0.587 * px[i + 1] + 0.114 * px[i + 2];
        double mean = sum / (w * h) * 1.02;
        for (int y = 0; y < h; y++)
        {
            double k = 1 + Sheen * (0.5 - y / (double)(h - 1)) * 0.6;
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (px[i + 3] == 0) continue;
                double r = px[i] * Bright, g = px[i + 1] * Bright, b = px[i + 2] * Bright;
                r = mean + (r - mean) * Contrast; g = mean + (g - mean) * Contrast; b = mean + (b - mean) * Contrast;
                double l = 0.299 * r + 0.587 * g + 0.114 * b;
                r = (l + (r - l) * Colour) * k; g = (l + (g - l) * Colour) * k; b = (l + (b - l) * Colour) * k;
                px[i] = (byte)Math.Clamp(r, 0, 255); px[i + 1] = (byte)Math.Clamp(g, 0, 255); px[i + 2] = (byte)Math.Clamp(b, 0, 255);
            }
        }
        System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
    }

    /// <summary>Decodes the picture and trims the empty margin around the car, so cards can size it by the car itself.</summary>
    static SKImage? Trim(byte[] png, bool photo = false)
    {
        // 2026-10-28: decode from an SKData, not "new SKMemoryStream(png)": the codec takes the stream over, and the
        // stream's finalizer freed it a second time - a segfault on a garbage collection or at exit (found by the bench, #483)
        using var pngData = SKData.CreateCopy(png);
        using var codec = SKCodec.Create(pngData);
        if (codec == null) return null;
        using var bmp = SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (bmp == null) return null;
        if (photo) Grade(bmp, 1.16, 1.10, 1.12, 0.3);   // sim photos: real light already, just lifted (overcast tracks read dark)
        else Grade(bmp);
        int x0 = bmp.Width, y0 = bmp.Height, x1 = -1, y1 = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Alpha > 16) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
        if (x1 < x0 || y1 < y0) return null;
        // 2026-10-28 (#483): copy the pixels INTO the image. SKImage.FromBitmap(sub.Copy()) could keep pointing at the
        // copy's pixels, which were freed when that never-disposed copy was collected - a blank car or a crash later.
        using var sub = new SKBitmap();
        if (!bmp.ExtractSubset(sub, SKRectI.Create(x0, y0, x1 - x0 + 1, y1 - y0 + 1))) return SKImage.FromPixelCopy(bmp.Info, bmp.GetPixels(), bmp.RowBytes);
        return SKImage.FromPixelCopy(sub.Info, sub.GetPixels(), sub.RowBytes);
    }
}

/// <summary>Minimal TGA reader for iRacing paint files: uncompressed or RLE true-colour, 24 or 32 bit, either origin.</summary>
public static class Tga
{
    public static SKBitmap? Decode(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        if (b.Length < 18) return null;
        int idLen = b[0], cmapType = b[1], type = b[2];
        int cmapLen = b[5] | (b[6] << 8), cmapDepth = b[7];
        int w = b[12] | (b[13] << 8), h = b[14] | (b[15] << 8), bpp = b[16], desc = b[17];
        if ((type != 2 && type != 10) || (bpp != 24 && bpp != 32) || w <= 0 || h <= 0) return null;
        int px = bpp / 8, pos = 18 + idLen + (cmapType == 1 ? cmapLen * ((cmapDepth + 7) / 8) : 0);
        bool topDown = (desc & 0x20) != 0;
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var dst = new byte[w * h * 4];
        int total = w * h, n = 0;
        void Put(int i, int o)
        {
            int x = i % w, y = i / w; if (!topDown) y = h - 1 - y;
            int d = (y * w + x) * 4;
            dst[d] = b[o + 2]; dst[d + 1] = b[o + 1]; dst[d + 2] = b[o]; dst[d + 3] = px == 4 ? (byte)255 : (byte)255;   // paint alpha is a spec mask: ignore
        }
        if (type == 2)
        {
            for (; n < total && pos + px <= b.Length; n++, pos += px) Put(n, pos);
        }
        else
        {
            while (n < total && pos < b.Length)
            {
                int hdr = b[pos++], count = (hdr & 0x7F) + 1;
                if ((hdr & 0x80) != 0) { for (int k = 0; k < count && n < total; k++) Put(n++, pos); pos += px; }
                else for (int k = 0; k < count && n < total && pos + px <= b.Length; k++, pos += px) Put(n++, pos);
            }
        }
        System.Runtime.InteropServices.Marshal.Copy(dst, 0, bmp.GetPixels(), dst.Length);
        return bmp;
    }
}
