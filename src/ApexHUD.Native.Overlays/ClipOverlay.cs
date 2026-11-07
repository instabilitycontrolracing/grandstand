using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// 2026-11-06 (Angelo: "do the vertical clip stuff"): the see-through 1080x1920 picture laid over a vertical clip (a
/// YouTube Short / TikTok / Reel). The race picture sits across the middle (the host scales it to 1080 wide, centred,
/// over a blurred, darkened copy of itself that fills the tall frame). Above it: where and when (gold, tracked), the moment
/// in big letters with a gold hairline; below it: the driver and the channel name, and a small "Broadcast by Grandstand".
/// </summary>
public static class ClipOverlay
{
    public const int W = 1080, H = 1920;

    /// <param name="videoH">How tall the race picture is once scaled to 1080 wide (608 for 16:9).</param>
    public static byte[] Render(string eyebrow, string title, string sub, string handle, int videoH)
    {
        var info = new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surf = SKSurface.Create(info);
        var c = surf.Canvas;
        c.Clear(SKColors.Transparent);
        float vTop = (H - videoH) / 2f, vBot = vTop + videoH;
        var gold = new SKColor(0xE8, 0xC2, 0x5A); var goldHi = new SKColor(0xFF, 0xD8, 0x76);
        var ivory = new SKColor(252, 250, 245); var txt2 = new SKColor(0xD3, 0xD9, 0xE3);

        // soft dark bands top and bottom so the words read on any picture
        using (var sh = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, vTop),
                   new[] { new SKColor(4, 6, 12, 150), new SKColor(4, 6, 12, 60) }, null, SKShaderTileMode.Clamp) })
            c.DrawRect(0, 0, W, vTop, sh);
        using (var sh = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, vBot), new SKPoint(0, H),
                   new[] { new SKColor(4, 6, 12, 60), new SKColor(4, 6, 12, 170) }, null, SKShaderTileMode.Clamp) })
            c.DrawRect(0, vBot, W, H - vBot, sh);
        // the picture's frame: gold hairlines top and bottom, fading at the ends
        using (var ln = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(W, 0),
                   new[] { gold.WithAlpha(0), goldHi, gold.WithAlpha(0) }, null, SKShaderTileMode.Clamp) })
        {
            c.DrawRect(0, vTop - 3, W, 3, ln);
            c.DrawRect(0, vBot, W, 3, ln);
        }

        // above: eyebrow, the moment, hairline
        float y = vTop - 150;
        if (eyebrow.Length > 0)
            using (var p = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 30, Color = gold })
                Tracked(c, eyebrow.ToUpperInvariant(), W / 2f, y - 110, 6f, p, 980);
        using (var p = new SKPaint { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 112, Color = ivory, TextAlign = SKTextAlign.Center })
        {
            string t = title.ToUpperInvariant();
            while (p.TextSize > 54 && p.MeasureText(t) > 980) p.TextSize -= 2;
            using (var glow = new SKPaint { IsAntialias = true, Typeface = p.Typeface, TextSize = p.TextSize, TextAlign = SKTextAlign.Center, Color = new SKColor(0, 0, 0, 150), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14) })
                c.DrawText(t, W / 2f, y, glow);
            c.DrawText(t, W / 2f, y, p);
        }
        using (var hl = new SKPaint { Color = gold }) c.DrawRect(W / 2f - 80, y + 34, 160, 4, hl);

        // below: the driver, then the channel and the small credit
        float by = vBot + 120;
        if (sub.Length > 0)
            using (var p = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 40, Color = txt2, TextAlign = SKTextAlign.Center })
            {
                while (p.TextSize > 24 && p.MeasureText(sub) > 980) p.TextSize -= 1;
                c.DrawText(sub, W / 2f, by, p);
            }
        if (handle.Length > 0)
            using (var p = new SKPaint { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 64, Color = ivory, TextAlign = SKTextAlign.Center })
            {
                while (p.TextSize > 32 && p.MeasureText(handle) > 980) p.TextSize -= 2;
                c.DrawText(handle, W / 2f, H - 260, p);
            }
        using (var p = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 20, Color = gold.WithAlpha(210) })
            Tracked(c, "BROADCAST BY GRANDSTAND", W / 2f, H - 196, 5f, p, 900);

        using var img = surf.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    static void Tracked(SKCanvas c, string s, float cx, float y, float track, SKPaint p, float maxW)
    {
        float Width() => p.MeasureText(s) + Math.Max(0, s.Length - 1) * track;
        while (p.TextSize > 14 && Width() > maxW) p.TextSize -= 1;
        float x = cx - Width() / 2f;
        foreach (char ch in s)
        {
            string g = ch.ToString();
            c.DrawText(g, x, y, p);
            x += p.MeasureText(g) + track;
        }
    }
}
