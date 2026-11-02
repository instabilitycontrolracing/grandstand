using System.Globalization;
using System.IO;
using System.Text.Json;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// 2026-10-27 (visual pack, V1-V8 / W8-W13): the shared look and the no-garbage plumbing every new graphic uses.
/// <para>Look (taken from the shipped graphics): deep glass (#0F131C -> #04070B) with an accent top line and a soft
/// glow under it, gold #C5A572 / #E8C25A, warm ivory type, Barlow Condensed headlines, tracked Inter labels, Cormorant
/// for names and big numerals, slanted edges like the story cards, the navy plate of the opener / loading splash.</para>
/// <para>Cost rule: everything that does not move is drawn ONCE when a graphic is shown (<see cref="VpLayer"/>: a
/// recorded picture, rasterised at the screen's real scale and pinned on the graphics card); a frame is then a few image
/// draws plus the moving parts, with paints / paths / shaders kept in fields. Text that changes while a graphic is up
/// comes from <see cref="VpStr"/> (cached strings) through the per-thread text-blob cache, so a steady frame allocates
/// nothing.</para>
/// </summary>
public static class Vp
{
    // ---- palette ---------------------------------------------------------------------------------------------
    public static readonly SKColor Ivory = new(0xF4, 0xF6, 0xFA), Ivory2 = new(0xFC, 0xFA, 0xF5), Txt2 = new(0xB8, 0xC0, 0xCE),
        Txt3 = new(0x7E, 0x8A, 0xA0), Gold = new(0xC5, 0xA5, 0x72), GoldHi = new(0xE8, 0xC2, 0x5A), GoldPale = new(0xE2, 0xC9, 0x9A),
        GlassTop = new(0x0F, 0x13, 0x1C), GlassBot = new(0x04, 0x07, 0x0B), Glass = new(0x08, 0x0A, 0x10),
        Navy0 = new(0x0B, 0x1C, 0x36), Navy1 = new(0x03, 0x08, 0x14), NavyHi = new(0x12, 0x2C, 0x52), TitleGlow = new(0x9F, 0xC4, 0xFF),
        Red = new(0xD8, 0x1E, 0x2A), LiveRed = new(0xE5, 0x23, 0x2F), Purple = new(0xB0, 0x7C, 0xFF), Storm = new(0x4F, 0xB3, 0xFF),
        StormPale = new(0x9F, 0xD6, 0xFF), Amber = new(0xFF, 0xB4, 0x1F), Yellow = new(0xFF, 0xC1, 0x07), Gain = new(0x34, 0xD3, 0x99),
        Loss = new(0xF8, 0x71, 0x71), RcBlue = new(46, 92, 255), Warm = new(0xFF, 0xF4, 0xE0), PbGreen = new(0x3D, 0xDC, 0x84);

    /// <summary>Device pixels per design pixel at the last draw (2 on the 4K TV). New layers are baked at this scale.</summary>
    public static float LastScale = 1f;

    public static float DeviceScale(SKCanvas c)
    {
        var m = c.TotalMatrix;
        float s = MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
        return s <= 0.01f ? 1f : s;
    }

    // ---- easing ----------------------------------------------------------------------------------------------
    public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    public static float Seg(float t, float a, float b) => Clamp01((t - a) / (b - a));
    public static float Seg(double t, double a, double b) => Clamp01((float)((t - a) / (b - a)));
    public static float EaseIo(float v) { v = Clamp01(v); return v * v * (3f - 2f * v); }
    public static float EaseOut(float v) { v = 1f - Clamp01(v); return 1f - v * v * v; }
    public static float EaseIn(float v) { v = Clamp01(v); return v * v * v; }
    public static float EaseOutBack(float v) { v = Clamp01(v); const float c1 = 1.70158f, c3 = c1 + 1f; float x = v - 1f; return 1f + c3 * x * x * x + c1 * x * x; }
    public static float EaseOutExpo(float v) { v = Clamp01(v); return v >= 1f ? 1f : 1f - MathF.Pow(2f, -10f * v); }
    public static byte A(float a) => (byte)(255f * Clamp01(a) + 0.5f);
    public static SKColor WithA(this SKColor c, float a) => c.WithAlpha(A(a * c.Alpha / 255f));
    public static SKColor Mix(SKColor a, SKColor b, float t)
    {
        t = Clamp01(t);
        return new SKColor((byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t),
                           (byte)(a.Blue + (b.Blue - a.Blue) * t), (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));
    }

    [ThreadStatic] static Dictionary<string, SKColor?>? hexCache;

    /// <summary>"#RRGGBB" to a colour (parsed once per distinct string, so it is safe to call every frame).</summary>
    public static SKColor Hex(string? hex, SKColor fallback)
    {
        if (hex == null) return fallback;
        var cache = hexCache ??= new Dictionary<string, SKColor?>();
        if (!cache.TryGetValue(hex, out var col))
        {
            var s = hex.Trim().TrimStart('#');
            col = s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
                ? new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v) : null;
            if (cache.Count > 256) cache.Clear();
            cache[hex] = col;
        }
        return col ?? fallback;
    }

    /// <summary>A class colour that reads as a line on dark glass (GTP is black on the IMSA broadcast: ivory instead).</summary>
    public static SKColor ClassLine(string? classColorHex)
    {
        var c = Hex(classColorHex, Gold);
        return c.Red + c.Green + c.Blue < 90 ? Ivory : c;
    }

    // ---- bake-time drawing (allocation is fine here: it runs once per graphic shown) ---------------------------

    /// <summary>A parallelogram: left and right edges lean by <paramref name="slant"/> (top shifted right).</summary>
    public static SKPath Slant(SKRect r, float slant, bool leftEdge = true, bool rightEdge = true)
    {
        var p = new SKPath();
        p.MoveTo(r.Left + (leftEdge ? slant : 0), r.Top);
        p.LineTo(r.Right, r.Top);
        p.LineTo(r.Right - (rightEdge ? slant : 0), r.Bottom);
        p.LineTo(r.Left, r.Bottom);
        p.Close();
        return p;
    }

    /// <summary>The house glass panel: soft shadow, top-to-bottom glass, an accent line with a glow under it, a hairline rim.</summary>
    public static void GlassPanel(SKCanvas c, SKPath shape, SKColor accent, float glassAlpha = 0.93f, bool shadow = true, float accentH = 2f)
    {
        var b = shape.Bounds;
        if (shadow)
            using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 120), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14f) })
            { c.Save(); c.Translate(0, 6); c.DrawPath(shape, sh); c.Restore(); }
        using (var g = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, b.Top), new SKPoint(0, b.Bottom),
                   new[] { GlassTop.WithA(glassAlpha), GlassBot.WithA(glassAlpha) }, null, SKShaderTileMode.Clamp) })
            c.DrawPath(shape, g);
        c.Save();
        c.ClipPath(shape, SKClipOperation.Intersect, true);
        using (var glow = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, b.Top), new SKPoint(0, b.Top + 26),
                   new[] { accent.WithAlpha(90), accent.WithAlpha(0) }, null, SKShaderTileMode.Clamp) })
            c.DrawRect(new SKRect(b.Left, b.Top, b.Right, b.Top + 26), glow);
        using (var line = new SKPaint { Color = accent }) c.DrawRect(new SKRect(b.Left, b.Top, b.Right, b.Top + accentH), line);
        c.Restore();
        using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = new SKColor(255, 255, 255, 16) })
            c.DrawPath(shape, rim);
    }

    /// <summary>The navy plate of the opener / loading splash (radial navy, faint dot field), in design units.</summary>
    public static void NavyPlate(SKCanvas c, SKRect r, float dotStep = 60f)
    {
        using (var bg = new SKPaint { IsDither = true, Shader = SKShader.CreateRadialGradient(new SKPoint(r.MidX, r.Top + r.Height * 0.46f),
                   r.Width * 0.75f, new[] { NavyHi, Navy0, Navy1 }, new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp) })
            c.DrawRect(r, bg);
        using var dot = new SKPaint { IsAntialias = true, Color = Ivory.WithAlpha(13) };
        for (float gy = r.Top + dotStep; gy < r.Bottom; gy += dotStep)
            for (float gx = r.Left + dotStep; gx < r.Right; gx += dotStep)
                c.DrawCircle(gx, gy, 1.1f, dot);
    }

    public static void Hairline(SKCanvas c, float x0, float x1, float y, SKColor col, float alpha = 1f)
    {
        using var p = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(x0, 0), new SKPoint(x1, 0),
            new[] { col.WithAlpha(0), col.WithA(alpha), col.WithA(alpha), col.WithAlpha(0) }, new[] { 0f, 0.15f, 0.85f, 1f }, SKShaderTileMode.Clamp) };
        c.DrawRect(new SKRect(x0, y, x1, y + 1f), p);
    }

    /// <summary>A white chip with a dark car number (the tower / radio card chip).</summary>
    public static float NumberChip(SKCanvas c, string num, float x, float midY, float h, SKColor? fill = null, SKColor? ink = null)
    {
        using var nt = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = h * 0.6f, Color = ink ?? new SKColor(8, 10, 16) };
        float w = Math.Max(h * 1.55f, nt.MeasureText(num) + h * 0.6f);
        var chip = new SKRect(x, midY - h / 2, x + w, midY + h / 2);
        using (var cb = new SKPaint { IsAntialias = true, Color = fill ?? SKColors.White }) c.DrawRoundRect(chip, 2, 2, cb);
        nt.GetFontMetrics(out var m);
        c.DrawText(num, chip.MidX - nt.MeasureText(num) / 2f, chip.MidY - (m.Ascent + m.Descent) / 2f, nt);
        return w;
    }

    /// <summary>"LIVE" red pill used by the story cards / V5, in its own colours.</summary>
    public static float LivePill(SKCanvas c, float x, float midY, float h, string word = "LIVE")
    {
        using var tp = new SKPaint { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = h * 0.62f, Color = SKColors.White };
        float tw = VpText.Width(word, Fonts.BarlowCondensed.Bold, h * 0.62f, h * 0.06f);
        float w = h * 0.62f + h * 0.36f + tw + h * 0.42f;
        using (var bg = new SKPaint { IsAntialias = true, Color = LiveRed }) c.DrawRoundRect(new SKRect(x, midY - h / 2, x + w, midY + h / 2), 3, 3, bg);
        using (var dot = new SKPaint { IsAntialias = true, Color = SKColors.White }) c.DrawCircle(x + h * 0.42f, midY, h * 0.13f, dot);
        VpText.Draw(c, word, x + h * 0.7f, midY + h * 0.22f, Fonts.BarlowCondensed.Bold, h * 0.62f, SKColors.White, h * 0.06f);
        return w;
    }
}

/// <summary>Letter-spaced text for baked layers (allocates: bake time only).</summary>
public static class VpText
{
    public static float Width(string s, SKTypeface tf, float size, float track = 0f)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        using var p = new SKPaint { Typeface = tf, TextSize = size, SubpixelText = true };
        if (track == 0f) return p.MeasureText(s);
        var w = p.GetGlyphWidths(s);
        float x = 0; foreach (var a in w) x += a;
        return x + track * Math.Max(0, s.Length - 1);
    }

    /// <summary>Largest size (down to <paramref name="min"/>) at which <paramref name="s"/> fits <paramref name="maxW"/>.</summary>
    public static float Fit(string s, SKTypeface tf, float size, float maxW, float track = 0f, float min = 8f)
    {
        while (size > min && Width(s, tf, size, track) > maxW) size -= 0.5f;
        return size;
    }

    /// <summary>Draws text with optional tracking. align: -1 left, 0 centre, 1 right. Returns the drawn width.</summary>
    public static float Draw(SKCanvas c, string s, float x, float y, SKTypeface tf, float size, SKColor col, float track = 0f, int align = -1, float blur = 0f)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        using var p = new SKPaint { IsAntialias = true, Typeface = tf, TextSize = size, Color = col, SubpixelText = true };
        if (blur > 0) p.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur);
        float w = Width(s, tf, size, track);
        float x0 = align == 0 ? x - w / 2 : align > 0 ? x - w : x;
        if (track == 0f) { c.DrawText(s, x0, y, p); return w; }
        using var font = new SKFont(tf, size) { Subpixel = true };
        var widths = p.GetGlyphWidths(s);
        var glyphs = p.GetGlyphs(s);
        int n = Math.Min(glyphs.Length, widths.Length);
        var at = new SKPoint[n];
        float cx = 0;
        for (int i = 0; i < n; i++) { at[i] = new SKPoint(cx, 0); cx += widths[i] + track; }
        using var b = new SKTextBlobBuilder();
        b.AddPositionedRun(glyphs.AsSpan(0, n), font, at);
        using var blob = b.Build();
        if (blob != null) c.DrawText(blob, x0, y, p);
        return w;
    }

    /// <summary>Cap height of a face at a size (for vertical centring).</summary>
    public static float CapHeight(SKTypeface tf, float size)
    {
        using var p = new SKPaint { Typeface = tf, TextSize = size };
        p.GetFontMetrics(out var m);
        return m.CapHeight > 0 ? m.CapHeight : -m.Ascent * 0.7f;
    }
}

/// <summary>Cached strings for numbers that change while a graphic is up (formatted once per distinct value).</summary>
public static class VpStr
{
    static readonly Dictionary<long, string> cache = new();

    // no lambdas here: a closure would be allocated on every call, even when the string is already cached
    static bool Hit(long key, out string s) => cache.TryGetValue(key, out s!);
    static string Put(long key, string s) { if (cache.Count > 20000) cache.Clear(); cache[key] = s; return s; }

    /// <summary>"0.4" style, one decimal; "+0.4" with a sign when <paramref name="signed"/>.</summary>
    public static string F1(double v, bool signed = false)
    {
        long k = (long)Math.Round(v * 10), key = (1L << 40) ^ (k * 2 + (signed ? 1 : 0));
        return Hit(key, out var s) ? s : Put(key, (signed && k > 0 ? "+" : "") + (k / 10.0).ToString("0.0", CultureInfo.InvariantCulture));
    }

    public static string F2(double v, bool signed = false)
    {
        long k = (long)Math.Round(v * 100), key = (2L << 40) ^ (k * 2 + (signed ? 1 : 0));
        return Hit(key, out var s) ? s : Put(key, (signed && k > 0 ? "+" : "") + (k / 100.0).ToString("0.00", CultureInfo.InvariantCulture));
    }

    public static string F3(double v, bool signed = false)
    {
        long k = (long)Math.Round(v * 1000), key = (3L << 40) ^ (k * 2 + (signed ? 1 : 0));
        return Hit(key, out var s) ? s : Put(key, (signed && k > 0 ? "+" : "") + (k / 1000.0).ToString("0.000", CultureInfo.InvariantCulture));
    }

    public static string Int(long v)
    {
        long key = (4L << 40) ^ v;
        return Hit(key, out var s) ? s : Put(key, v.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>"P3"</summary>
    public static string Pos(int p)
    {
        long key = (5L << 40) ^ p;
        return Hit(key, out var s) ? s : Put(key, "P" + p.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>m:ss clock ("0:42", "12:05"), or h:mm:ss over an hour.</summary>
    public static string Clock(double seconds)
    {
        long t = (long)Math.Max(0, Math.Floor(seconds)), key = (6L << 40) ^ t;
        return Hit(key, out var s) ? s : Put(key, t >= 3600 ? $"{t / 3600}:{t / 60 % 60:00}:{t % 60:00}" : $"{t / 60}:{t % 60:00}");
    }

    /// <summary>Lap time "3:28.119".</summary>
    public static string Lap(double seconds)
    {
        long ms = (long)Math.Round(seconds * 1000), key = (7L << 40) ^ ms;
        return Hit(key, out var s) ? s : Put(key, ms <= 0 ? "-" : $"{ms / 60000}:{ms / 1000 % 60:00}.{ms % 1000:000}");
    }
}

/// <summary>
/// Numbers that change every frame (a live gap) drawn without making a string: the value is formatted into a stack
/// buffer and each character comes from the per-thread text-blob cache (one cached blob per character). Honours the
/// paint's TextAlign.
/// </summary>
public static class VpNum
{
    static readonly string[] chars = MakeChars();
    static string[] MakeChars()
    {
        var a = new string[128];
        for (int i = 32; i < 128; i++) a[i] = ((char)i).ToString();
        return a;
    }

    public static float Width(double value, int decimals, SKPaint p, bool signed = false)
    {
        Span<char> buf = stackalloc char[24];
        int n = Format(value, decimals, signed, buf);
        float w = 0;
        for (int i = 0; i < n; i++) w += TextBlobs.Measure(chars[buf[i] & 127] ?? " ", p);
        return w;
    }

    public static float Draw(SKCanvas c, double value, int decimals, float x, float y, SKPaint p, bool signed = false)
    {
        Span<char> buf = stackalloc char[24];
        int n = Format(value, decimals, signed, buf);
        var align = p.TextAlign;
        p.TextAlign = SKTextAlign.Left;
        float w = 0;
        for (int i = 0; i < n; i++) w += TextBlobs.Measure(chars[buf[i] & 127] ?? " ", p);
        float cx = align == SKTextAlign.Left ? x : align == SKTextAlign.Center ? x - w / 2 : x - w;
        for (int i = 0; i < n; i++)
        {
            var s = chars[buf[i] & 127] ?? " ";
            TextBlobs.Draw(c, s, cx, y, p);
            cx += TextBlobs.Measure(s, p);
        }
        p.TextAlign = align;
        return w;
    }

    static int Format(double v, int decimals, bool signed, Span<char> buf)
    {
        int n = 0;
        if (signed && v > 0) buf[n++] = '+';
        var fmt = decimals switch { 0 => "0", 1 => "0.0", 2 => "0.00", _ => "0.000" };
        v.TryFormat(buf[n..], out int written, fmt, CultureInfo.InvariantCulture);
        return n + written;
    }
}

/// <summary>
/// A static picture of part of a graphic: recorded once (vector), rasterised at the screen's real scale on first use
/// (and again only if the scale changes), moved onto the graphics card, then drawn every frame as one image with alpha.
/// </summary>
public sealed class VpLayer : IDisposable
{
    readonly SKPicture picture;
    public SKRect Bounds { get; }
    SKImage? image;
    float imageScale;
    [ThreadStatic] static SKPaint? paint;

    VpLayer(SKPicture p, SKRect b) { picture = p; Bounds = b; }

    /// <summary>Records <paramref name="draw"/> (design units) clipped to <paramref name="bounds"/>.</summary>
    public static VpLayer Record(SKRect bounds, Action<SKCanvas> draw)
    {
        bounds = new SKRect(MathF.Floor(bounds.Left), MathF.Floor(bounds.Top), MathF.Ceiling(bounds.Right), MathF.Ceiling(bounds.Bottom));
        using var rec = new SKPictureRecorder();
        var c = rec.BeginRecording(bounds);
        draw(c);
        var l = new VpLayer(rec.EndRecording(), bounds);
        l.Prepare(Vp.LastScale);
        return l;
    }

    /// <summary>Rasterise now at <paramref name="scale"/> (called when shown, so the first frame has nothing to do).</summary>
    public void Prepare(float scale)
    {
        scale = MathF.Round(Math.Clamp(scale, 0.25f, 4f) * 8f) / 8f;
        if (image != null && Math.Abs(imageScale - scale) < 0.01f) return;
        int pw = Math.Max(1, (int)MathF.Ceiling(Bounds.Width * scale)), ph = Math.Max(1, (int)MathF.Ceiling(Bounds.Height * scale));
        using var surf = SKSurface.Create(new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surf == null) return;
        surf.Canvas.Clear(SKColors.Transparent);
        surf.Canvas.Scale(scale);
        surf.Canvas.Translate(-Bounds.Left, -Bounds.Top);
        surf.Canvas.DrawPicture(picture);
        var raster = surf.Snapshot();
        var pinned = GpuContext.Pin(raster);
        if (!ReferenceEquals(pinned, raster)) raster.Dispose();
        image?.Dispose();
        image = pinned;
        imageScale = scale;
    }

    /// <summary>Draws the layer offset by (dx, dy) design units with an opacity.</summary>
    public void Draw(SKCanvas c, float dx, float dy, float alpha)
    {
        if (alpha <= 0.002f) return;
        // the screen's scale for this frame (set by the graphic before it transforms the canvas) - never the current
        // matrix, which a 3D flip or tilt squashes and would otherwise re-rasterise the layer every frame
        float s = Vp.LastScale;
        if (image == null || Math.Abs(imageScale - MathF.Round(Math.Clamp(s, 0.25f, 4f) * 8f) / 8f) > 0.01f) Prepare(s);
        var p = paint ??= new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
        p.Color = new SKColor(255, 255, 255, Vp.A(alpha));
        if (image == null) return;
        c.DrawImage(image, new SKRect(Bounds.Left + dx, Bounds.Top + dy, Bounds.Right + dx, Bounds.Bottom + dy), p);
    }

    /// <summary>Draws the layer into a destination rect (for zooms / stretches).</summary>
    public void DrawTo(SKCanvas c, SKRect dst, float alpha)
    {
        if (alpha <= 0.002f) return;
        if (image == null) Prepare(Vp.LastScale);
        if (image == null) return;
        var p = paint ??= new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
        p.Color = new SKColor(255, 255, 255, Vp.A(alpha));
        c.DrawImage(image, dst, p);
    }

    public void Dispose() { image?.Dispose(); image = null; picture.Dispose(); }
}

/// <summary>A recorded track line (assets\maps JSON: {"w","h","points":[[x,y],...]} in lap order from the line).</summary>
public sealed class VpTrack
{
    public readonly SKPoint[] Points;     // normalised to a 0..1 box keeping the aspect (longest side = 1)
    public readonly float Aspect;         // width / height
    /// <summary>Back to the JSON's own map units (what TrackTerrain's ground is laid out in): unit = point * Scale + Min.</summary>
    public float MinX { get; private init; }
    public float MinY { get; private init; }
    public float Scale { get; private init; } = 1f;

    VpTrack(SKPoint[] pts, float aspect) { Points = pts; Aspect = aspect; }

    /// <summary>The line in the map JSON's own units (for the 3D track-card system and its terrain).</summary>
    public SKPath PathRaw()
    {
        var path = new SKPath();
        path.MoveTo(MinX + Points[0].X * Scale, MinY + Points[0].Y * Scale);
        for (int i = 1; i < Points.Length; i++) path.LineTo(MinX + Points[i].X * Scale, MinY + Points[i].Y * Scale);
        path.Close();
        return path;
    }

    public static VpTrack? Load(string? file)
    {
        try
        {
            if (file == null || !File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var list = new List<SKPoint>();
            foreach (var p in doc.RootElement.GetProperty("points").EnumerateArray()) list.Add(new SKPoint(p[0].GetSingle(), p[1].GetSingle()));
            if (list.Count < 8) return null;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var p in list) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
            float bw = Math.Max(1e-3f, maxX - minX), bh = Math.Max(1e-3f, maxY - minY), s = Math.Max(bw, bh);
            var pts = new SKPoint[list.Count];
            for (int i = 0; i < pts.Length; i++) pts[i] = new SKPoint((list[i].X - minX) / s, (list[i].Y - minY) / s);
            return new VpTrack(pts, bw / bh) { MinX = minX, MinY = minY, Scale = s };
        }
        catch { return null; }
    }

    /// <summary>The line fitted (centred, aspect kept) into <paramref name="box"/>.</summary>
    public SKPath PathIn(SKRect box)
    {
        var (ox, oy, s) = Fit(box);
        var path = new SKPath();
        path.MoveTo(ox + Points[0].X * s, oy + Points[0].Y * s);
        for (int i = 1; i < Points.Length; i++) path.LineTo(ox + Points[i].X * s, oy + Points[i].Y * s);
        path.Close();
        return path;
    }

    public (float Ox, float Oy, float S) Fit(SKRect box)
    {
        float w = Aspect >= 1 ? 1f : Aspect, h = Aspect >= 1 ? 1f / Aspect : 1f;
        float s = Math.Min(box.Width / w, box.Height / h);
        return (box.MidX - w * s / 2, box.MidY - h * s / 2, s);
    }
}

/// <summary>Position along a fitted track path by lap fraction (cached measure; allocation-free lookups).</summary>
public sealed class VpTrackPath : IDisposable
{
    public readonly SKPath Path;
    readonly SKPathMeasure measure;
    public readonly float Length;

    public VpTrackPath(SKPath path) { Path = path; measure = new SKPathMeasure(path, true); Length = measure.Length; }

    public SKPoint At(float pct)
    {
        pct -= MathF.Floor(pct);
        return measure.GetPosition(pct * Length, out var p) ? p : default;
    }

    public SKPoint TangentAt(float pct)
    {
        pct -= MathF.Floor(pct);
        return measure.GetTangent(pct * Length, out var t) ? t : new SKPoint(1, 0);
    }

    /// <summary>Lap fractions f0..f1 (may wrap past the line) into <paramref name="into"/>.</summary>
    public bool Segment(float f0, float f1, SKPath into)
    {
        into.Reset();
        f0 -= MathF.Floor(f0); f1 -= MathF.Floor(f1);
        if (f1 <= 0f) f1 = 1f;
        if (f0 < f1) return measure.GetSegment(f0 * Length, f1 * Length, into, true);
        bool a = measure.GetSegment(f0 * Length, Length, into, true);
        bool b = measure.GetSegment(0, f1 * Length, into, true);
        return a || b;
    }

    public void Dispose() { measure.Dispose(); Path.Dispose(); }
}

/// <summary>The base for every visual-pack graphic: a style switch and the show / draw contract the compositor uses.</summary>
public abstract class VpGraphic : IDisposable
{
    /// <summary>false = the base design, true = the elevated ("v2") design.</summary>
    public bool Elevated { get; set; }

    /// <summary>Something is on screen or still animating (the compositor keeps presenting while true).</summary>
    public abstract bool Active(double nowMs);

    /// <summary>Draws in the compositor's 1080p design space (the host scales the canvas up for 4K).</summary>
    public abstract void Draw(SKCanvas c, int w, int h, double nowMs);

    /// <summary>Hides it now (animated).</summary>
    public virtual void Hide(double nowMs) { }

    public virtual void Dispose() { }
}
