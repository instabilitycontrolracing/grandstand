using System.Text;
using SkiaSharp;

/// <summary>Stand-ins for the assets a clone doesn't have (the globe pictures, logos, the Le Mans map,
/// fonts), written into the bench's own output folder. Deterministic (fixed seeds), so a before and an
/// after build render the same pictures. Real files win: anything already present is left alone, and
/// the repo's assets/globe is linked in when it exists.</summary>
static class FakeAssets
{
    public static void Ensure(string root, bool globe = true)
    {
        Directory.CreateDirectory(root);
        var repo = AppContext.BaseDirectory;
        while (repo != null && !File.Exists(Path.Combine(repo, "DECISIONS.md"))) repo = Path.GetDirectoryName(repo.TrimEnd(Path.DirectorySeparatorChar));
        repo ??= AppContext.BaseDirectory;
        Fonts(Path.Combine(root, "fonts"));
        var realGlobe = Path.Combine(repo, "assets", "globe");
        if (File.Exists(Path.Combine(realGlobe, "tiles.json")) && !Directory.Exists(Path.Combine(root, "globe")))
            Directory.CreateSymbolicLink(Path.Combine(root, "globe"), realGlobe);
        if (globe) Globe(Path.Combine(root, "globe"));
        else if (Directory.Exists(Path.Combine(root, "globe"))) throw new InvalidOperationException("--noglobe needs its own OUT folder (this one has globe pictures)");
        Logo(Path.Combine(root, "logos", "iracing-brand", "iRacing-Inline-Color-White.png"), 1200, 260, "iRacing", SKColors.Transparent, SKColors.White, 190);
        Logo(Path.Combine(root, "logos", "iracing-brand", "iRacing-Inline-BW-White.png"), 1200, 260, "iRacing", SKColors.Transparent, SKColors.White, 190);
        Logo(Path.Combine(root, "logos", "series", "IMSA iRacing Series.png"), 1000, 480, "IMSA", SKColors.Transparent, new SKColor(0xE8, 0x2A, 0x3A), 300);
        // a logo on a solid navy plate, like the real Le Mans one (exercises the matte / key-out path)
        Logo(Path.Combine(root, "logos", "tracks", "24 Heures du Mans.png"), 900, 500, "24H LE MANS", new SKColor(0x0B, 0x1F, 0x4A), SKColors.White, 130);
        Logo(Path.Combine(root, "flags", "fr.png"), 90, 60, "", new SKColor(0x00, 0x23, 0x95), SKColors.White, 10, flag: true);
        foreach (var b in new[] { "BMW", "Porsche", "Ferrari", "Mercedes", "Audi", "Lamborghini", "Mclaren", "Aston", "Ford" })
            Logo(Path.Combine(root, "logos", "manufacturers", b + ".png"), 400, 400, b[..Math.Min(3, b.Length)].ToUpperInvariant(), SKColors.Transparent, SKColors.White, 150);
        foreach (var (k, col) in new[] { ("GTP", new SKColor(0xC0, 0x84, 0xFC)), ("LMP2", new SKColor(0x60, 0xA5, 0xFA)), ("GTD", new SKColor(0xFF, 0xDA, 0x59)), ("GTDPRO", new SKColor(0xFF, 0xB6, 0x27)) })
            Logo(Path.Combine(root, "logos", "classes", k + ".png"), 500, 200, k, SKColors.Transparent, col, 140);
        Map(Path.Combine(root, "maps", "Circuit des 24 Heures du Mans.json"));
    }

    static void Fonts(string dir)
    {
        string[] src = { "/usr/share/fonts/truetype/google-fonts", "/usr/share/fonts/truetype/dejavu" };
        (string Sub, string File, string[] Try)[] want =
        {
            ("cormorant-garamond", "CormorantGaramond-Regular.ttf", new[] { "CormorantGaramond-Regular.ttf", "CormorantGaramond-SemiBold.ttf" }),
            ("cormorant-garamond", "CormorantGaramond-Medium.ttf", new[] { "CormorantGaramond-Medium.ttf", "CormorantGaramond-SemiBold.ttf" }),
            ("cormorant-garamond", "CormorantGaramond-SemiBold.ttf", new[] { "CormorantGaramond-SemiBold.ttf" }),
            ("cormorant-garamond", "CormorantGaramond-Bold.ttf", new[] { "CormorantGaramond-Bold.ttf" }),
            ("inter", "Inter-Regular.ttf", new[] { "Inter-Regular.ttf" }),
            ("inter", "Inter-Medium.ttf", new[] { "Inter-Medium.ttf" }),
            ("inter", "Inter-SemiBold.ttf", new[] { "Inter-SemiBold.ttf" }),
            ("inter", "Inter-Bold.ttf", new[] { "Inter-Bold.ttf" }),
            ("barlow-condensed", "BarlowCondensed-Medium.ttf", new[] { "BarlowCondensed-Medium.ttf", "BarlowCondensed-SemiBold.ttf" }),
            ("barlow-condensed", "BarlowCondensed-SemiBold.ttf", new[] { "BarlowCondensed-SemiBold.ttf" }),
            ("barlow-condensed", "BarlowCondensed-Bold.ttf", new[] { "BarlowCondensed-Bold.ttf" }),
            ("barlow-condensed", "BarlowCondensed-ExtraBold.ttf", new[] { "BarlowCondensed-ExtraBold.ttf" }),
            ("barlow-condensed", "BarlowCondensed-Black.ttf", new[] { "BarlowCondensed-Black.ttf", "BarlowCondensed-ExtraBold.ttf" }),
        };
        foreach (var (sub, file, tries) in want)
        {
            var dst = Path.Combine(dir, sub, file);
            if (File.Exists(dst)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            var hit = tries.SelectMany(t => src.Select(s => Path.Combine(s, t))).FirstOrDefault(File.Exists)
                      ?? Path.Combine(src[1], "DejaVuSans-Bold.ttf");
            File.Copy(hit, dst);
        }
    }

    // ---------------------------------------------------------------- globe (GlobeGT format)
    static void Globe(string dir)
    {
        if (File.Exists(Path.Combine(dir, "tiles.json"))) return;
        Directory.CreateDirectory(dir);
        const int GW = 4096, GH = 2048; double gppd = GW / 360.0;
        Earth(Path.Combine(dir, "global.jpg"), GW, GH, -180, 90, gppd);
        // a sharper picture of western Europe (the circuit's region)
        double lon0 = -8, lon1 = 14, lat0 = 40, lat1 = 56, ppd = 50;
        Earth(Path.Combine(dir, "detail_weurope.jpg"), (int)((lon1 - lon0) * ppd), (int)((lat1 - lat0) * ppd), lon0, lat1, ppd);
        File.WriteAllText(Path.Combine(dir, "tiles.json"), string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[{{\"name\":\"global\",\"file\":\"global.jpg\",\"lon0\":-180,\"lon1\":180,\"lat0\":-90,\"lat1\":90,\"ppd\":{gppd}}}," +
            $"{{\"name\":\"weurope\",\"file\":\"detail_weurope.jpg\",\"lon0\":{lon0},\"lon1\":{lon1},\"lat0\":{lat0},\"lat1\":{lat1},\"ppd\":{ppd}}}]"));
        // labels: [name, lat, lon, rank] countries and [name, lat, lon, adm] regions
        var rnd = new Random(5);
        var sb = new StringBuilder("{\"countries\":[");
        (string, double, double, double)[] real =
        {
            ("France", 46.6, 2.4, 1.2), ("Spain", 40.2, -3.6, 1.5), ("Germany", 51.1, 10.4, 1.4), ("Italy", 42.8, 12.6, 1.6),
            ("United Kingdom", 53.0, -1.8, 1.7), ("Poland", 52.1, 19.4, 2.2), ("Portugal", 39.6, -8.0, 2.6), ("Switzerland", 46.8, 8.2, 3.1),
            ("Belgium", 50.6, 4.6, 3.3), ("Netherlands", 52.2, 5.5, 3.2), ("Austria", 47.6, 14.1, 3.0), ("Ireland", 53.2, -8.1, 3.0),
            ("Russia", 61.0, 90.0, 0.5), ("China", 35.0, 103.0, 0.6), ("India", 22.0, 79.0, 0.9), ("Kazakhstan", 48.0, 67.0, 1.4),
            ("Turkey", 39.0, 35.0, 1.8), ("Ukraine", 49.0, 31.4, 1.9), ("Algeria", 28.0, 2.6, 1.1), ("Morocco", 31.8, -6.8, 2.2),
            ("Andorra", 42.5, 1.5, 6.0), ("Luxembourg", 49.8, 6.1, 5.0), ("Monaco", 43.7, 7.4, 7.0),
        };
        var list = real.ToList();
        for (int i = 0; i < 160; i++) list.Add(($"Country {i}", -60 + rnd.NextDouble() * 130, -180 + rnd.NextDouble() * 360, 0.8 + rnd.NextDouble() * 5));
        sb.Append(string.Join(",", list.Select(c => System.FormattableString.Invariant($"[\"{c.Item1}\",{c.Item2:0.000},{c.Item3:0.000},{c.Item4:0.00}]"))));
        sb.Append("],\"regions\":[");
        var regs = new List<string>();
        string[] fr = { "Pays de la Loire", "Normandie", "Bretagne", "Centre-Val de Loire", "Ile-de-France", "Nouvelle-Aquitaine", "Hauts-de-France", "Grand Est", "Bourgogne", "Auvergne", "Occitanie", "Provence" };
        for (int i = 0; i < 60; i++)
        {
            string n = i < fr.Length ? fr[i] : $"Departement {i}";
            regs.Add(System.FormattableString.Invariant($"[\"{n}\",{44 + rnd.NextDouble() * 7:0.000},{-3 + rnd.NextDouble() * 9:0.000},\"FRA\"]"));
        }
        for (int i = 0; i < 400; i++)
            regs.Add(System.FormattableString.Invariant($"[\"Region {i}\",{-50 + rnd.NextDouble() * 110:0.000},{-170 + rnd.NextDouble() * 340:0.000},\"X{i % 40}\"]"));
        sb.Append(string.Join(",", regs)).Append("]}");
        File.WriteAllText(Path.Combine(dir, "labels.json"), sb.ToString());
    }

    static double Hash(int x, int y, int s) { unchecked { uint h = (uint)(x * 374761393 + y * 668265263 + s * 2147483647); h = (h ^ (h >> 13)) * 1274126177; return ((h ^ (h >> 16)) & 0xFFFFFF) / (double)0xFFFFFF; } }
    static double Noise(double x, double y, int s)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y); double fx = x - xi, fy = y - yi;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        double a = Hash(xi, yi, s), b = Hash(xi + 1, yi, s), c = Hash(xi, yi + 1, s), d = Hash(xi + 1, yi + 1, s);
        return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
    }
    static double Fbm(double lon, double lat)
    {
        // seamless in longitude enough for a bench: sample on the sphere's (x,y,z) projected to 2 planes
        double v = 0, amp = 0.5, f = 1 / 18.0;
        for (int o = 0; o < 6; o++) { v += amp * Noise((lon + 180) * f, (lat + 90) * f, o); amp *= 0.5; f *= 2; }
        return v;
    }
    static void Earth(string path, int w, int h, double lonLeft, double latTop, double ppd)
    {
        if (File.Exists(path)) return;
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var px = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            double lat = latTop - (y + 0.5) / ppd;
            for (int x = 0; x < w; x++)
            {
                double lon = lonLeft + (x + 0.5) / ppd;
                double n = Fbm(lon, lat);
                bool land = n > 0.53;
                double m = Fbm(lon * 3 + 50, lat * 3 + 20);
                byte r, g, b;
                if (land) { double k = 0.6 + 0.6 * m; r = (byte)Math.Clamp(28 * k, 0, 255); g = (byte)Math.Clamp(42 * k, 0, 255); b = (byte)Math.Clamp(66 * k, 0, 255); }
                else { double k = 0.8 + 0.3 * n; r = (byte)(18 * k); g = (byte)(86 * k); b = (byte)(98 * k); }
                if (land && Math.Abs(n - 0.53) < 0.004) { r = 120; g = 150; b = 170; }   // coast line
                int i = (y * w + x) * 4; px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 255;
            }
        });
        System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 88);
        File.WriteAllBytes(path, data.ToArray());
    }

    // ---------------------------------------------------------------- logos, flag, map
    static void Logo(string path, int w, int h, string text, SKColor bg, SKColor fg, float size, bool flag = false)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(bg);
            if (flag)
            {
                using var wp = new SKPaint { Color = SKColors.White }; c.DrawRect(w / 3f, 0, w / 3f, h, wp);
                using var rp = new SKPaint { Color = new SKColor(0xED, 0x29, 0x39) }; c.DrawRect(2 * w / 3f, 0, w / 3f, h, rp);
            }
            else
            {
                using var tf = SKTypeface.FromFile("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf");
                using var p = new SKPaint { Color = fg, IsAntialias = true, TextSize = size, Typeface = tf, TextAlign = SKTextAlign.Center };
                float tw = p.MeasureText(text); if (tw > w * 0.9f) p.TextSize *= w * 0.9f / tw;
                c.DrawText(text, w / 2f, h / 2f + p.TextSize * 0.36f, p);
                using var bar = new SKPaint { Color = fg.WithAlpha(200), IsAntialias = true };
                c.DrawRoundRect(new SKRect(w * 0.1f, h * 0.82f, w * 0.9f, h * 0.86f), 4, 4, bar);
            }
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    static void Map(string path)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // a long loop with a big open infield (the logo goes inside), roughly Le Mans-shaped
        var pts = new List<string>();
        for (int i = 0; i < 600; i++)
        {
            double a = 2 * Math.PI * i / 600;
            double x = 600 + 520 * Math.Cos(a) + 40 * Math.Cos(3 * a), y = 330 - 270 * Math.Sin(a) + 30 * Math.Sin(5 * a) + (Math.Cos(a) > 0.6 ? 40 * Math.Sin(9 * a) : 0);
            pts.Add(System.FormattableString.Invariant($"[{x:0.0},{y:0.0}]"));
        }
        File.WriteAllText(path, $"{{\"w\":1200,\"h\":700,\"points\":[{string.Join(",", pts)}]}}");
    }
}
