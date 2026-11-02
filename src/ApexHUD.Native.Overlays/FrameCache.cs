using SkiaSharp;

namespace ApexHUD.Native.Overlays;

// 2026-10-24 (Angelo: "the intro is horribly choppy"): helpers that let a renderer draw a frame without
// creating Skia objects. In SkiaSharp every SKPaint (~1.3 KB of managed memory), SKShader, SKTextBlob (made by
// every DrawText(string) call) or SKRoundRect is a managed wrapper registered in a global handle table; dozens
// per frame made the opener allocate hundreds of KB per frame and collect garbage several times a second.
// Measured with tools/intro_bench. Render-thread helpers; the text cache is per thread.

/// <summary>A gradient (or picture) shader kept between frames and rebuilt only when its inputs change.
/// Ask for it every frame with the same arguments as the old SKShader.Create* call; collection
/// expressions (<c>[c0, c1]</c>) for the stops stay on the stack.</summary>
internal sealed class ShaderSlot : IDisposable
{
    SKShader? shader;
    byte kind;
    SKPoint p0, p1; float r0; SKShaderTileMode tx, ty; SKMatrix m; object? src;
    SKColor[] cols = new SKColor[4]; float[] pos = new float[4]; int n; bool hasPos;

    bool Same(byte k, SKPoint a, SKPoint b, float r, ReadOnlySpan<SKColor> c, ReadOnlySpan<float> p, SKShaderTileMode t1, SKShaderTileMode t2)
    {
        if (shader == null || kind != k || p0 != a || p1 != b || r0 != r || tx != t1 || ty != t2 || n != c.Length || hasPos != !p.IsEmpty) return false;
        for (int i = 0; i < n; i++) if (cols[i] != c[i]) return false;
        if (hasPos) for (int i = 0; i < n; i++) if (pos[i] != p[i]) return false;
        return true;
    }

    void Remember(byte k, SKPoint a, SKPoint b, float r, ReadOnlySpan<SKColor> c, ReadOnlySpan<float> p, SKShaderTileMode t1, SKShaderTileMode t2)
    {
        kind = k; p0 = a; p1 = b; r0 = r; tx = t1; ty = t2; n = c.Length; hasPos = !p.IsEmpty;
        if (cols.Length < n) { cols = new SKColor[n]; pos = new float[n]; }
        c.CopyTo(cols); if (hasPos) p.CopyTo(pos);
    }

    SKShader Swap(SKShader s) { shader?.Dispose(); shader = s; return s; }

    /// <summary>SKShader.CreateLinearGradient(a, b, colors, positions-or-null, mode), cached.</summary>
    public SKShader Linear(SKPoint a, SKPoint b, ReadOnlySpan<SKColor> colors, ReadOnlySpan<float> positions, SKShaderTileMode mode = SKShaderTileMode.Clamp)
    {
        if (Same(1, a, b, 0, colors, positions, mode, mode)) return shader!;
        Remember(1, a, b, 0, colors, positions, mode, mode);
        return Swap(SKShader.CreateLinearGradient(a, b, colors.ToArray(), positions.IsEmpty ? null : positions.ToArray(), mode));
    }

    /// <summary>SKShader.CreateRadialGradient(center, radius, colors, positions-or-null, mode), cached.</summary>
    public SKShader Radial(SKPoint center, float radius, ReadOnlySpan<SKColor> colors, ReadOnlySpan<float> positions, SKShaderTileMode mode = SKShaderTileMode.Clamp)
    {
        if (Same(2, center, default, radius, colors, positions, mode, mode)) return shader!;
        Remember(2, center, default, radius, colors, positions, mode, mode);
        return Swap(SKShader.CreateRadialGradient(center, radius, colors.ToArray(), positions.IsEmpty ? null : positions.ToArray(), mode));
    }

    /// <summary>SKShader.CreateBitmap(bitmap, tx, ty, matrix), cached (the bitmap itself is the key).</summary>
    public SKShader Bitmap(SKBitmap bmp, SKShaderTileMode tileX, SKShaderTileMode tileY, SKMatrix matrix)
    {
        if (shader != null && kind == 3 && ReferenceEquals(src, bmp) && tx == tileX && ty == tileY && m == matrix) return shader;
        kind = 3; src = bmp; tx = tileX; ty = tileY; m = matrix;
        return Swap(SKShader.CreateBitmap(bmp, tileX, tileY, matrix));
    }

    /// <summary>image.ToShader(tx, ty), cached per image.</summary>
    public SKShader Image(SKImage img, SKShaderTileMode tileX, SKShaderTileMode tileY)
    {
        if (shader != null && kind == 4 && ReferenceEquals(src, img) && tx == tileX && ty == tileY) return shader;
        kind = 4; src = img; tx = tileX; ty = tileY;
        return Swap(img.ToShader(tileX, tileY));
    }

    public void Dispose() { shader?.Dispose(); shader = null; src = null; }
}

/// <summary>Text drawn from cached text blobs: same pixels as SKCanvas.DrawText(string, ...) (checked
/// byte for byte), but no SKTextBlob / builder / run buffer is created on frames where the string,
/// typeface and size are the same as before.</summary>
internal static class TextBlobs
{
    readonly record struct Key(string Text, IntPtr Typeface, float Size, float ScaleX, float SkewX, int Flags, float Spacing, byte Mode);
    sealed class Entry { public SKTextBlob? Blob; public float Width; }

    const int Cap = 1536;   // per generation; two generations, the older one is dropped when the newer fills
    [ThreadStatic] static Dictionary<Key, Entry>? now;
    [ThreadStatic] static Dictionary<Key, Entry>? old;

    static Key KeyOf(string text, SKPaint p, float spacing, byte mode)
    {
        int flags = (p.IsAntialias ? 1 : 0) | (p.SubpixelText ? 2 : 0) | (p.LcdRenderText ? 4 : 0) | (p.FakeBoldText ? 8 : 0)
                  | (p.IsLinearText ? 16 : 0) | (p.IsAutohinted ? 32 : 0) | ((int)p.HintingLevel << 8);
        return new Key(text, p.Typeface?.Handle ?? IntPtr.Zero, p.TextSize, p.TextScaleX, p.TextSkewX, flags, spacing, mode);
    }

    static Entry Get(string text, SKPaint p, float spacing, byte mode)
    {
        now ??= new(); old ??= new();
        var k = KeyOf(text, p, spacing, mode);
        if (now.TryGetValue(k, out var e)) return e;
        if (old.Remove(k, out e)) { Add(k, e); return e; }
        e = Build(text, p, spacing, mode);
        Add(k, e);
        return e;
    }

    static void Add(Key k, Entry e)
    {
        if (now!.Count >= Cap)
        {
            foreach (var x in old!.Values) x.Blob?.Dispose();   // a blob already handed to Skia is ref-counted there
            old.Clear();
            (now, old) = (old, now);
        }
        now[k] = e;
    }

    static Entry Build(string text, SKPaint p, float spacing, byte mode)
    {
        var e = new Entry();
        using var font = p.ToFont();
        if (mode == 0)
        {
            e.Width = p.MeasureText(text);
            e.Blob = SKTextBlob.Create(text, font);
            return e;
        }
        // one glyph per character at the same x positions the old per-character DrawText loops used
        var adv = new float[text.Length];
        if (mode == 1)
        {
            var w = p.GetGlyphWidths(text);   // TextTracked's advances
            for (int i = 0; i < adv.Length; i++) adv[i] = i < w.Length ? w[i] : 0;
        }
        else
            for (int i = 0; i < adv.Length; i++) adv[i] = p.MeasureText(text[i].ToString());   // GlobeGT's spaced captions
        var glyphs = new ushort[text.Length];
        var at = new SKPoint[text.Length];
        float x = 0;
        for (int i = 0; i < text.Length; i++) { glyphs[i] = font.GetGlyph(text[i]); at[i] = new SKPoint(x, 0); x += adv[i] + spacing; }
        if (mode == 1) { float total = 0; for (int i = 0; i < adv.Length; i++) total += adv[i] + (i == adv.Length - 1 ? 0 : spacing); e.Width = total; }
        else { float total = 0; foreach (var a in adv) total += a; e.Width = total + spacing * Math.Max(0, text.Length - 1); }
        if (text.Length > 0)
        {
            using var b = new SKTextBlobBuilder();
            b.AddPositionedRun(glyphs, font, at);
            e.Blob = b.Build();
        }
        return e;
    }

    /// <summary>Drop-in for <c>c.DrawText(text, x, y, p)</c> (honours p.TextAlign the same way).</summary>
    public static void Draw(SKCanvas c, string? text, float x, float y, SKPaint p)
    {
        if (string.IsNullOrEmpty(text)) return;
        var e = Get(text, p, 0, 0);
        if (e.Blob == null) return;
        var align = p.TextAlign;
        if (align != SKTextAlign.Left) x -= align == SKTextAlign.Center ? e.Width * 0.5f : e.Width;
        c.DrawText(e.Blob, x, y, p);
    }

    /// <summary>p.MeasureText(text), cached alongside the blob.</summary>
    public static float Measure(string text, SKPaint p) => string.IsNullOrEmpty(text) ? 0 : Get(text, p, 0, 0).Width;

    /// <summary>Letter-spaced text laid out exactly like TextTracked (glyph widths + spacing), as one blob.</summary>
    public static void DrawTracked(SKCanvas c, string text, float x, float y, float spacing, SKPaint p)
    {
        var e = Get(text, p, spacing, 1);
        if (e.Blob == null) return;
        float startX = x;
        if (p.TextAlign == SKTextAlign.Right) startX = x - e.Width;
        else if (p.TextAlign == SKTextAlign.Center) startX = x - e.Width / 2f;
        c.DrawText(e.Blob, startX, y, p);
    }

    /// <summary>Width of <see cref="DrawTracked"/> text: glyph widths + spacing between (the old MeasureTracked), cached with the blob.</summary>
    public static float TrackedWidth(string text, SKPaint p, float spacing) => string.IsNullOrEmpty(text) ? 0 : Get(text, p, spacing, 1).Width;

    /// <summary>Spaced capitals advanced by each character's MeasureText (GlobeGT's captions), as one blob.</summary>
    public static void DrawSpaced(SKCanvas c, string text, float x, float baseline, float spacing, SKPaint p)
    {
        var e = Get(text, p, spacing, 2);
        if (e.Blob != null) c.DrawText(e.Blob, x, baseline, p);
    }

    /// <summary>Width of <see cref="DrawSpaced"/> text: sum of per-character widths + spacing between.</summary>
    public static float SpacedWidth(string text, SKPaint p, float spacing) => Get(text, p, spacing, 2).Width;
}

/// <summary>A drop-shadow image filter kept between frames, rebuilt only when its offset / blur / colour change.</summary>
internal sealed class ShadowSlot : IDisposable
{
    SKImageFilter? f; float dx, dy, sx, sy; SKColor col; bool only;
    public SKImageFilter DropShadow(float dX, float dY, float sigmaX, float sigmaY, SKColor color, bool shadowOnly = false)
    {
        if (f != null && dx == dX && dy == dY && sx == sigmaX && sy == sigmaY && col == color && only == shadowOnly) return f;
        f?.Dispose();
        dx = dX; dy = dY; sx = sigmaX; sy = sigmaY; col = color; only = shadowOnly;
        return f = shadowOnly ? SKImageFilter.CreateDropShadowOnly(dX, dY, sigmaX, sigmaY, color) : SKImageFilter.CreateDropShadow(dX, dY, sigmaX, sigmaY, color);
    }
    public void Dispose() { f?.Dispose(); f = null; }
}

/// <summary>Small strings drawn every frame (numbers, single characters), made once.</summary>
internal static class SmallStrings
{
    static readonly string[] nums = Enumerable.Range(0, 1000).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    static readonly string[] chars = Enumerable.Range(0, 128).Select(i => ((char)i).ToString()).ToArray();
    public static string Num(int n) => n >= 0 && n < nums.Length ? nums[n] : n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static string Char(char c) => c < 128 ? chars[c] : c.ToString();
}

/// <summary>SkiaSharp 2.88's DrawBitmap wraps the bitmap in a new SKImage on every call; this keeps one image per
/// (immutable) bitmap so drawing it allocates nothing. Same pixels: the image shares the bitmap's pixels.</summary>
internal static class BitmapImages
{
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKBitmap, SKImage> images = new();

    public static SKImage? Of(SKBitmap bmp)
    {
        if (images.TryGetValue(bmp, out var img)) return img;
        if (!bmp.IsImmutable) return null;   // a bitmap that can still change must be re-wrapped each time
        img = SKImage.FromBitmap(bmp);
        if (img != null) images.AddOrUpdate(bmp, img);
        return img;
    }

    public static void Draw(SKCanvas c, SKBitmap bmp, SKRect dst, SKPaint p)
    {
        var img = Of(bmp);
        if (img != null) c.DrawImage(img, dst, p); else c.DrawBitmap(bmp, dst, p);
    }

    public static void Draw(SKCanvas c, SKBitmap bmp, SKRect src, SKRect dst, SKPaint p)
    {
        var img = Of(bmp);
        if (img != null) c.DrawImage(img, src, dst, p); else c.DrawBitmap(bmp, src, dst, p);
    }
}

/// <summary>Constant gradient stop lists. (A constant collection expression like <c>[0f, 0.5f, 1f]</c> was seen
/// allocating ~100 bytes per use per frame in the bench - RuntimeHelpers.CreateSpan's field-info stub - so the
/// constant ones live here.)</summary>
internal static class Stops
{
    public static readonly float[] At035 = { 0f, 0.35f, 1f }, At042 = { 0f, 0.42f, 1f }, At045 = { 0f, 0.45f, 1f }, At05 = { 0f, 0.5f, 1f },
        At055 = { 0f, 0.55f, 1f }, At06 = { 0f, 0.6f, 1f }, At082 = { 0f, 0.82f, 1f }, Hairline = { 0f, 0.15f, 0.5f, 0.85f, 1f };
}

/// <summary>2026-10-24 (choppy intro, 4K): very large text (the ghosted car / lap numbers, ~130-170 px, i.e.
/// 260-340 px on a 4K screen) is too big for Skia's glyph cache, so the GPU path redrew every glyph as a
/// shape on the CPU every frame. Here it is drawn once into a picture at the screen's real size and that
/// picture is reused (re-made only when the text, size or screen scale changes). Render thread only.</summary>
internal static class BigText
{
    readonly record struct Key(string Text, IntPtr Face, float Size, float Scale);
    sealed class Entry { public SKImage? Img; public SKRect Bounds; public long Used; }
    static readonly Dictionary<Key, Entry> cache = new();
    static long tick;
    static readonly SKPaint drawPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };

    /// <summary>Like c.DrawText(text, x, baseline, paint) for a solid-colour, fill-style paint with
    /// TextAlign Left/Center/Right.</summary>
    public static void Draw(SKCanvas c, string text, float x, float baseline, SKPaint paint)
    {
        if (string.IsNullOrEmpty(text)) return;
        var m = c.TotalMatrix;
        float s = MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
        s = MathF.Round(Math.Max(0.25f, s) * 20f) / 20f;
        var key = new Key(text, paint.Typeface?.Handle ?? IntPtr.Zero, paint.TextSize, s);
        if (!cache.TryGetValue(key, out var e))
        {
            if (cache.Count > 48) Trim();
            e = new Entry();
            using var p = new SKPaint { IsAntialias = true, Typeface = paint.Typeface, TextSize = paint.TextSize, Color = SKColors.White };
            var b = new SKRect(); p.MeasureText(text, ref b);
            b.Inflate(4, 4);
            int w = Math.Max(1, (int)MathF.Ceiling(b.Width * s)), h = Math.Max(1, (int)MathF.Ceiling(b.Height * s));
            using (var surf = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul)))
            {
                if (surf != null)
                {
                    surf.Canvas.Clear(SKColors.Transparent);
                    surf.Canvas.Scale(s);
                    surf.Canvas.DrawText(text, -b.Left, -b.Top, p);
                    e.Img = surf.Snapshot();
                }
            }
            e.Bounds = b;
            cache[key] = e;
        }
        e.Used = ++tick;
        if (e.Img == null) { c.DrawText(text, x, baseline, paint); return; }
        float adv = paint.TextAlign == SKTextAlign.Left ? 0f : paint.MeasureText(text) * (paint.TextAlign == SKTextAlign.Center ? 0.5f : 1f);
        var dst = new SKRect(x - adv + e.Bounds.Left, baseline + e.Bounds.Top, x - adv + e.Bounds.Right, baseline + e.Bounds.Bottom);
        // the picture is white: tint it to the paint's colour (alpha included)
        var col = paint.Color;
        drawPaint.ColorFilter = Tint(col);
        c.DrawImage(e.Img, dst, drawPaint);
    }

    static readonly Dictionary<SKColor, SKColorFilter> tints = new();
    static SKColorFilter Tint(SKColor col)
    {
        if (!tints.TryGetValue(col, out var f))
        {
            if (tints.Count > 64) { foreach (var v in tints.Values) v.Dispose(); tints.Clear(); }
            tints[col] = f = SKColorFilter.CreateBlendMode(col, SKBlendMode.SrcIn);
        }
        return f;
    }

    static void Trim()
    {
        foreach (var kv in cache.OrderBy(k => k.Value.Used).Take(cache.Count / 2).ToList())
        { kv.Value.Img?.Dispose(); cache.Remove(kv.Key); }
    }
}

/// <summary>2026-10-24 (choppy intro): the host's graphics context, so a renderer can keep a big picture
/// resident on the graphics card (SKImage.ToTextureImage) instead of letting Skia re-send it.</summary>
public static class GpuContext
{
    public static GRContext? Current;

    /// <summary>A copy of <paramref name="img"/> that lives on the graphics card (or the image itself when
    /// there is no card / it already does). Call on the render thread.</summary>
    public static SKImage Pin(SKImage img)
    {
        var ctx = Current;
        if (ctx == null || img.IsTextureBacked) return img;
        try { return img.ToTextureImage(ctx) ?? img; } catch { return img; }
    }
}
