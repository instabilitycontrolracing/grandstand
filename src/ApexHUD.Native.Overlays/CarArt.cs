using System.IO;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// The car picture on the Driver-to-Watch panel / card and the Your Race cockpit (2026-10-24, public build).
///
/// <para>Order: the owner's press photos (assets\logos\sponsors\car_*_panel.png, car_&lt;name&gt;.png - personal,
/// never shipped; looked up by the callers exactly as before) -&gt; a picture the user put in
/// <c>Documents\iRacing\Grandstand\cars\</c> (same file names, matched by the words in the name, png or jpg,
/// e.g. their own iRacing screenshot) -&gt; on a public install (no personal photos at all) an ORIGINAL drawn
/// panel: class-colour glass, the maker's logo large, the car's name in big type and speed lines.
/// On the owner's PC a car without a photo still shows no picture, as before.</para>
///
/// <para>Everything is decoded / drawn once per car and cached (immutable bitmaps, uploaded to the graphics
/// card once); nothing here runs per frame except a dictionary lookup.</para>
/// </summary>
public static class CarArt
{
    public static readonly string UserFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "cars");

    /// <summary>Size of the drawn panel (16:9; callers cover-fit it, so the middle always survives the crop).</summary>
    public const int ArtW = 1920, ArtH = 1080;
    const int MaxUserSide = 2560;
    const long MaxUserBytes = 40L * 1024 * 1024;

    static readonly Dictionary<string, SKBitmap?> cache = new(StringComparer.OrdinalIgnoreCase);
    static string[]? userFiles;
    static bool readmeDone;

    /// <summary>The user's picture for this car, or (public install only) the drawn panel. Null on the owner's PC
    /// when he has no photo for the car - the callers then behave exactly as before.</summary>
    public static SKBitmap? Fallback(string? carName, string? brand, string? className, string? classColor)
    {
        if (string.IsNullOrWhiteSpace(carName)) return null;
        var user = User(carName);
        if (user != null) return user;
        if (CarPhotos.PersonalInstalled) return null;
        return Drawn(carName, brand, className, classColor);
    }

    /// <summary>A picture from Documents\iRacing\Grandstand\cars matched to the car name, decoded once (scaled down
    /// to at most 2560 px), or null.</summary>
    public static SKBitmap? User(string? carName)
    {
        if (string.IsNullOrWhiteSpace(carName)) return null;
        userFiles ??= ListUser();
        var path = CarPhotos.FindIn(userFiles, carName);
        if (path == null) return null;
        if (cache.TryGetValue(path, out var hit)) return hit;
        SKBitmap? bmp = null;
        try
        {
            if (new FileInfo(path).Length <= MaxUserBytes)
            {
                using var codec = SKCodec.Create(path);
                if (codec != null)
                {
                    var info = codec.Info;
                    float s = Math.Min(1f, MaxUserSide / (float)Math.Max(info.Width, info.Height));
                    var full = SKBitmap.Decode(codec);
                    if (full != null && s < 1f)
                    {
                        var small = full.Resize(new SKImageInfo(Math.Max(1, (int)(info.Width * s)), Math.Max(1, (int)(info.Height * s))), SKFilterQuality.High);
                        full.Dispose();
                        full = small;
                    }
                    bmp = full;
                    bmp?.SetImmutable();
                }
            }
        }
        catch { bmp = null; }
        return cache[path] = bmp;
    }

    static string[] ListUser()
    {
        try
        {
            Directory.CreateDirectory(UserFolder);
            WriteReadme();
            return Directory.GetFiles(UserFolder, "car_*.*")
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    static void WriteReadme()
    {
        if (readmeDone) return;
        readmeDone = true;
        try
        {
            var readme = Path.Combine(UserFolder, "README.txt");
            if (File.Exists(readme)) return;
            File.WriteAllText(readme,
                "Grandstand - your own car pictures\r\n" +
                "==================================\r\n\r\n" +
                "The Driver to Watch panel and the Your Race card show a picture of your car. Grandstand draws one\r\n" +
                "itself (your class colour, the maker's logo, the car's name). To use your own picture instead -\r\n" +
                "for example an iRacing screenshot of your car - save it here, named after the car:\r\n\r\n" +
                "  car_bmw_m4_gt3_panel.png        (fits \"BMW M4 GT3\" and \"BMW M4 GT3 EVO\")\r\n" +
                "  car_porsche_963_panel.jpg       (fits \"Porsche 963 GTP\")\r\n" +
                "  car_ferrari_296_gt3_panel.png\r\n\r\n" +
                "Start with car_, separate the words with _, end with _panel; every word must be in iRacing's car\r\n" +
                "name (the file with the most matching words wins). png or jpg, wide pictures (16:9) work best,\r\n" +
                "the middle of the picture is what stays in view. Restart Grandstand after adding pictures.\r\n" +
                "Only use pictures you took or have the rights to show on a stream.\r\n");
        }
        catch { }
    }

    // ------------------------------------------------------------------ the drawn panel

    /// <summary>The original drawn car panel (cached per car + class). Public so preview tools can render it.</summary>
    public static SKBitmap? Drawn(string carName, string? brand, string? className, string? classColor)
    {
        string key = "drawn|" + carName + "|" + className + "|" + classColor;
        if (cache.TryGetValue(key, out var hit)) return hit;
        SKBitmap? bmp = null;
        try { bmp = Render(carName, brand, className, classColor); bmp.SetImmutable(); }
        catch { bmp = null; }
        return cache[key] = bmp;
    }

    static SKColor Hex(string? hex, SKColor fallback) => !string.IsNullOrWhiteSpace(hex) && SKColor.TryParse(hex, out var c) ? c : fallback;

    static SKColor Mix(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t), (byte)(a.Blue + (b.Blue - a.Blue) * t), (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));

    /// <summary>Class colour for drawing light: very dark classes (GTP black) light up in silver.</summary>
    public static SKColor LightColor(SKColor c)
    {
        float lum = (0.2126f * c.Red + 0.7152f * c.Green + 0.0722f * c.Blue) / 255f;
        return lum < 0.12f ? new SKColor(214, 222, 232) : c;
    }

    /// <summary>"BMW M4 GT3 EVO" + brand "BMW" -> ("BMW", "M4 GT3 EVO").</summary>
    static (string Maker, string Model) SplitName(string carName, string? brand)
    {
        string name = carName.Trim();
        string maker = (brand ?? "").Trim();
        if (maker.Length > 0 && name.StartsWith(maker, StringComparison.OrdinalIgnoreCase))
            return (maker.ToUpperInvariant(), name[maker.Length..].Trim().ToUpperInvariant());
        int sp = name.IndexOf(' ');
        if (maker.Length == 0 && sp > 0) return (name[..sp].ToUpperInvariant(), name[(sp + 1)..].ToUpperInvariant());
        return (maker.ToUpperInvariant(), name.ToUpperInvariant());
    }

    static SKBitmap Render(string carName, string? brand, string? className, string? classColor)
    {
        const int W = ArtW, H = ArtH;
        string cls = (className ?? "").Trim().ToUpperInvariant();
        var baseCol = Hex(ClassColors.For(cls, classColor ?? (cls.Length > 0 ? BroadcastPalette.Class(cls) : "#C5A572")), new SKColor(0xC5, 0xA5, 0x72));
        var light = LightColor(baseCol);
        var (maker, model) = SplitName(carName, brand);
        if (model.Length == 0) model = maker;

        var bmp = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var c = new SKCanvas(bmp);
        var rnd = new Random(carName.GetHashCode() & 0x7fffffff ^ 0x5eed);

        // 1. ground: deep diagonal gradient, tinted by the class
        var deep = new SKColor(5, 8, 12);
        using (var p = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(W, H),
                   new[] { Mix(deep, baseCol, 0.42f), Mix(deep, baseCol, 0.16f), deep }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp) })
            c.DrawRect(0, 0, W, H, p);
        using (var p = new SKPaint { Shader = SKShader.CreateRadialGradient(new SKPoint(W * 0.62f, H * 0.46f), W * 0.6f,
                   new[] { light.WithAlpha(70), light.WithAlpha(18), SKColors.Transparent }, new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp),
                   BlendMode = SKBlendMode.Plus })
            c.DrawRect(0, 0, W, H, p);

        // 2. speed lines, slanted, denser through the middle band
        c.Save();
        c.Translate(W / 2f, H / 2f); c.RotateDegrees(-9); c.Translate(-W / 2f, -H / 2f);
        using (var lp = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Plus })
            for (int i = 0; i < 90; i++)
            {
                float y = (float)(H * (0.5 + (rnd.NextDouble() + rnd.NextDouble() + rnd.NextDouble() - 1.5) * 0.55));
                float len = 200 + (float)rnd.NextDouble() * 900, x = (float)rnd.NextDouble() * (W + len) - len;
                float th = 1 + (float)rnd.NextDouble() * (i % 9 == 0 ? 7 : 2.5f);
                byte al = (byte)(20 + rnd.Next(i % 9 == 0 ? 70 : 45));
                var col = i % 3 == 0 ? SKColors.White : light;
                lp.Shader = SKShader.CreateLinearGradient(new SKPoint(x, 0), new SKPoint(x + len, 0),
                    new[] { col.WithAlpha(0), col.WithAlpha(al), col.WithAlpha(0) }, new[] { 0f, 0.8f, 1f }, SKShaderTileMode.Clamp);
                c.DrawRoundRect(new SKRect(x, y - th / 2, x + len, y + th / 2), th / 2, th / 2, lp);
                lp.Shader?.Dispose(); lp.Shader = null;
            }
        c.Restore();

        // 3. the model name, huge, leaning forward: outlined ghost + a solid core line
        var face = Fonts.BarlowCondensed.Black;
        using (var tp = new SKPaint { IsAntialias = true, Typeface = face, TextSize = 400, TextAlign = SKTextAlign.Center })
        {
            float maxW = W * 0.96f;
            while (tp.TextSize > 120 && tp.MeasureText(model) > maxW) tp.TextSize -= 8;
            float base0 = H * 0.5f + tp.TextSize * 0.36f;
            c.Save();
            c.Skew(-0.2f, 0); c.Translate(base0 * 0.2f, 0);
            tp.Shader = SKShader.CreateLinearGradient(new SKPoint(0, base0 - tp.TextSize * 0.75f), new SKPoint(0, base0),
                new[] { SKColors.White.WithAlpha(46), light.WithAlpha(20) }, null, SKShaderTileMode.Clamp);
            c.DrawText(model, W * 0.5f, base0, tp);
            tp.Shader.Dispose(); tp.Shader = null;
            tp.Style = SKPaintStyle.Stroke; tp.StrokeWidth = 3; tp.Color = SKColors.White.WithAlpha(110);
            c.DrawText(model, W * 0.5f, base0, tp);
            c.Restore();
        }

        // 4. glass band across the middle with a class-colour edge
        using (var band = new SKPath())
        {
            float top = H * 0.30f, bot = H * 0.70f, k = 90;
            band.MoveTo(-40, top + k); band.LineTo(W + 40, top - k); band.LineTo(W + 40, bot - k); band.LineTo(-40, bot + k); band.Close();
            using (var gp = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, top - k), new SKPoint(0, bot + k),
                       new[] { SKColors.White.WithAlpha(30), SKColors.White.WithAlpha(8), SKColors.White.WithAlpha(16) }, new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp) })
                c.DrawPath(band, gp);
            using var ep = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = SKColors.White.WithAlpha(90) };
            c.DrawLine(-40, top + k, W + 40, top - k, ep);
            ep.Color = light.WithAlpha(230); ep.StrokeWidth = 7;
            c.DrawLine(-40, bot + k, W + 40, bot - k, ep);
            ep.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14); ep.StrokeWidth = 18; ep.Color = light.WithAlpha(120);
            c.DrawLine(-40, bot + k, W + 40, bot - k, ep);
        }

        // 5. the maker's logo, large, in the middle (callers crop the sides)
        var slug = ManufacturerBrand.Resolve(brand ?? maker);
        SKBitmap? logo = null;
        try { if (slug != null) { var lpth = Assets.Resolve("logos", "manufacturers", slug + ".png"); if (File.Exists(lpth)) logo = SKBitmap.Decode(lpth); } } catch { logo = null; }
        if (logo != null)
        {
            using (logo)
            {
                float s = Math.Min(560f / logo.Width, 300f / logo.Height), lw = logo.Width * s, lh = logo.Height * s;
                var dst = new SKRect(W / 2f - lw / 2, H * 0.47f - lh / 2, W / 2f + lw / 2, H * 0.47f + lh / 2);
                using var sp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High,
                    ImageFilter = SKImageFilter.CreateDropShadow(0, 10, 24, 24, new SKColor(0, 0, 0, 170)) };
                c.DrawBitmap(logo, dst, sp);
            }
        }
        else
        {
            using var mp = new SKPaint { IsAntialias = true, Typeface = Fonts.BarlowCondensed.ExtraBold, TextSize = 190, TextAlign = SKTextAlign.Center, Color = SKColors.White,
                ImageFilter = SKImageFilter.CreateDropShadow(0, 8, 20, 20, new SKColor(0, 0, 0, 170)) };
            TextTracked.Draw(c, mp, maker.Length > 0 ? maker : model, W / 2f, H * 0.47f + 66, 14);
        }

        // 6. small caption lower right: full car name + class chip
        using (var cp = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 30, Color = SKColors.White.WithAlpha(210), TextAlign = SKTextAlign.Center })
        {
            string cap = carName.Trim().ToUpperInvariant();
            float y = H * 0.78f;
            TextTracked.Draw(c, cp, cap, W * 0.5f, y, 4);
            if (cls.Length > 0)
            {
                string disp = ClassLogo.DisplayName(cls);
                using var chip = new SKPaint { IsAntialias = true, Color = light };
                using var ct = new SKPaint { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 30, TextAlign = SKTextAlign.Center,
                    Color = (0.2126f * light.Red + 0.7152f * light.Green + 0.0722f * light.Blue) > 150 ? new SKColor(8, 10, 14) : SKColors.White };
                float tw = ct.MeasureText(disp) + 28;
                var rr = new SKRect(W / 2f - tw / 2, y + 22, W / 2f + tw / 2, y + 62);
                c.DrawRoundRect(rr, 6, 6, chip);
                c.DrawText(disp, rr.MidX, rr.Bottom - 10, ct);
            }
        }

        // 7. film grain + vignette
        using (var gp = new SKPaint { BlendMode = SKBlendMode.Overlay })
            for (int i = 0; i < 9000; i++)
            {
                gp.Color = (rnd.Next(2) == 0 ? SKColors.White : SKColors.Black).WithAlpha((byte)rnd.Next(10, 34));
                float x = (float)rnd.NextDouble() * W, y = (float)rnd.NextDouble() * H;
                c.DrawRect(x, y, 2, 2, gp);
            }
        using (var vp = new SKPaint { Shader = SKShader.CreateRadialGradient(new SKPoint(W / 2f, H / 2f), W * 0.72f,
                   new[] { SKColors.Transparent, SKColors.Transparent, new SKColor(0, 0, 0, 150) }, new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp) })
            c.DrawRect(0, 0, W, H, vp);
        c.Flush();
        return bmp;
    }
}
