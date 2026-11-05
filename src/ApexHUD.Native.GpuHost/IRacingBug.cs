using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// #773 (2026-11-05, Angelo): the iRacing logo, always on screen (iRacing Broadcast Policy, Dec 15 2021, s.2(a):
/// a permanent part of the stream, live and replays, in one of the four corners, at least 185x45 on a 1920x1080
/// canvas, approved iRacing artwork only). Angelo: small, fades in, as see-through as we can get away with -> the
/// official white logo at 45 px tall (242x45), 25 % visible. Top-right normally; while the session bar is full width
/// (battle look, pre-race, post-race) it fades out and comes back bottom-right. Drawn in 1080p layout units.
/// </summary>
internal sealed class IRacingBug : IDisposable
{
    public const float H = 45f, Margin = 24f;
    public const float Opacity = 0.25f;
    const double FadeInMs = 1000, FadeOutMs = 350, SwitchHoldMs = 300;

    SKBitmap? logo; bool tried;
    readonly SKPaint paint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.High };
    double firstMs = -1, wantSinceMs = -1, lastMs = -1;
    bool bottom, wantBottom;
    float k;            // 0..1 visibility (before Opacity)
    bool switching;     // fading out to change corners

    public float W => logo == null ? 242f : H * logo.Width / logo.Height;
    public SKRect LastRect { get; private set; }
    /// <summary>True while it is fading (keep presenting).</summary>
    public bool Animating => firstMs < 0 || switching || k < 0.999f;

    /// <summary>Which corner it wants: bottom-right while the bar is full width and on screen (or the post-race pages).</summary>
    static bool WantsBottom() =>
        PresentationState.BarFullWidth && PresentationState.OnboardK < 0.5f;

    public void Draw(SKCanvas c, int w, int h, double nowMs)
    {
        if (!tried)
        {
            tried = true;
            try { logo = Assets.LoadBitmap("logos", "iracing-brand", "iRacing-Inline-BW-White.png"); }
            catch (Exception ex) { Program.Log("iRacing logo: " + ex.Message); }
        }
        if (logo == null) return;
        if (firstMs < 0) { firstMs = nowMs; bottom = wantBottom = WantsBottom(); }
        double dt = lastMs < 0 ? 16 : Math.Clamp(nowMs - lastMs, 0, 100);
        lastMs = nowMs;

        bool want = WantsBottom();
        if (want != wantBottom) { wantBottom = want; wantSinceMs = nowMs; }
        if (wantBottom != bottom && !switching && nowMs - wantSinceMs >= SwitchHoldMs) switching = true;

        if (switching)
        {
            k = Math.Max(0f, k - (float)(dt / FadeOutMs));
            if (k <= 0f) { bottom = wantBottom; switching = false; }
        }
        else k = Math.Min(1f, k + (float)(dt / FadeInMs));

        float lw = W;
        float x = w - Margin - lw;
        float y = bottom ? h - Margin - H : Margin;
        var dst = new SKRect(x, y, x + lw, y + H);
        LastRect = dst;
        float e = k * k * (3 - 2 * k);   // smoothstep
        if (e <= 0.002f) return;
        paint.Color = new SKColor(255, 255, 255, (byte)Math.Round(255 * Opacity * e));
        c.DrawBitmap(logo, dst, paint);
    }

    public void Dispose() { paint.Dispose(); logo?.Dispose(); }
}
