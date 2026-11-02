using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// #336 (2026-10-27, Angelo): the broadcast watch showing the REAL local time (the PC's clock) - top middle on the TV
/// session review, the loading splash and the going-to-break card. It is the session bar's own watch (<see cref="WatchRenderer"/>
/// wearing the dial picked in <see cref="ClockDial"/> - his personal dial on his PC, the bundled homage elsewhere), never a
/// redrawn one.
/// <para>Cost: the dial, subdials, bezel, drop shadow and the small LOCAL TIME caption are baked ONCE into one picture at
/// the screen's real scale (again only if the scale or the chosen dial changes); a frame is that one image plus the three
/// hands - no allocation.</para>
/// </summary>
public sealed class LocalWatch : IDisposable
{
    readonly WatchRenderer watch = new();
    SKImage? face;
    float faceScale = -1, faceRadius = -1;
    int faceVersion = int.MinValue;
    bool faceCaption;
    readonly SKPaint img = new() { IsAntialias = true, FilterQuality = SKFilterQuality.High };
    readonly SKPaint layer = new();
    const float Pad = 26f;
    float CaptionH => 40f * CaptionScale;

    /// <summary>#438: size of the caption relative to the default (the loading splash's huge watch uses a bigger one).</summary>
    public float CaptionScale { get; set; } = 1f;

    /// <summary>Caption under the watch (baked). Empty = none.</summary>
    public string Caption { get; set; } = "LOCAL TIME";

    /// <summary>Keep the baked face on the MAIN overlay's graphics card. False for the TV splash / review window, which has
    /// its own GL context (a texture from the other context can't be drawn there).</summary>
    public bool PinToGpu { get; set; } = true;

    /// <summary>Test / preview hook: the time shown (null = the PC clock, <see cref="DateTime.Now"/>).</summary>
    public Func<DateTime>? Clock;

    /// <summary>Draws the watch centred at (cx, cy), outer radius r (design units of the canvas), at an opacity.</summary>
    public void Draw(SKCanvas c, float cx, float cy, float r, float alpha)
    {
        if (alpha <= 0.003f) return;
        float scale = Vp.DeviceScale(c);
        bool caption = Caption.Length > 0;
        if (face == null || Math.Abs(scale - faceScale) > 0.01f || Math.Abs(r - faceRadius) > 0.01f || faceVersion != WatchRenderer.FaceVersion || caption != faceCaption)
            Bake(scale, r, caption);
        var now = Clock?.Invoke() ?? DateTime.Now;
        int save = -1;
        if (alpha < 0.997f)
        {
            layer.Color = new SKColor(0, 0, 0, Vp.A(alpha));
            save = c.SaveLayer(new SKRect(cx - r - Pad, cy - r - Pad, cx + r + Pad, cy + r + Pad + (caption ? CaptionH : 0)), layer);
        }
        if (face != null)
            c.DrawImage(face, new SKRect(cx - r - Pad, cy - r - Pad, cx + r + Pad, cy + r + Pad + (caption ? CaptionH : 0)), img);
        watch.DrawHandsOnly(c, cx, cy, r, now);
        if (save >= 0) c.RestoreToCount(save);
    }

    void Bake(float scale, float r, bool caption)
    {
        faceScale = scale; faceRadius = r; faceVersion = WatchRenderer.FaceVersion; faceCaption = caption;
        float wD = 2 * (r + Pad), hD = 2 * (r + Pad) + (caption ? CaptionH : 0);
        int pw = Math.Max(1, (int)MathF.Ceiling(wD * scale)), ph = Math.Max(1, (int)MathF.Ceiling(hD * scale));
        using var surf = SKSurface.Create(new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surf == null) return;
        var c = surf.Canvas;
        c.Clear(SKColors.Transparent);
        c.Scale(scale);
        float cx = r + Pad, cy = r + Pad;
        // the session bar's watch shadow (soft, slightly below)
        using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 120), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, r * 0.09f) })
            c.DrawCircle(cx, cy + r * 0.06f, r * 0.99f, sh);
        watch.DrawFace(c, cx, cy, r);
        if (caption)
            VpText.Draw(c, Caption, cx, cy + r + Math.Clamp(r * 0.36f, 16f, 30f) * CaptionScale, Fonts.Inter.SemiBold, Math.Clamp(r * 0.15f, 9f, 15f) * CaptionScale, Vp.GoldPale.WithAlpha(210), Math.Clamp(r * 0.06f, 2f, 5f) * CaptionScale, 0);
        var raster = surf.Snapshot();
        var pinned = PinToGpu ? GpuContext.Pin(raster) : raster;
        if (!ReferenceEquals(pinned, raster)) raster.Dispose();
        face?.Dispose();
        face = pinned;
    }

    public void Dispose() { face?.Dispose(); watch.Dispose(); img.Dispose(); layer.Dispose(); }
}
