using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// #505 - the Michelin pit card's pictures ready on the graphics card before the card comes in.
///
/// <para>dep49 (2026-10-29) decoded the brand pictures and the running-man frames ahead and gave each one invisible draw,
/// once per run. Two things undid it: (1) the host's graphics-memory diet (#519) drops every picture nothing has drawn
/// for 2 minutes, and the pit card usually comes 30+ minutes into a race - so by then all of it had been thrown away
/// and was uploaded again (and its smaller sizes rebuilt) in the card's first frames; (2) the warm draw used plain
/// sampling while the card draws them smoothed, so the smoothed copy (with its smaller sizes) was still built on the
/// card's first frames even right after the warm-up.</para>
///
/// <para>Now: the same pictures, drawn exactly the way the card draws them (smoothed), one per frame while no story card
/// is up, and touched again every <see cref="RefreshMs"/> so the diet never drops them. The closing logo ("michelin
/// outro") is included. Cost: one 1x1, 1/255-alpha draw per frame for a handful of frames a minute; ~25-35 MB of
/// graphics memory kept for the pit card during a session (only on a PC that has the pictures).</para>
/// </summary>
public static class PitCardWarm
{
    /// <summary>Re-touch interval; under the diet's 2-minute "unused" limit.</summary>
    public const double RefreshMs = 60_000;

    static readonly string[] Brands = { "michelin", "michelin man", "michelin man full", "michelin outro" };
    // the card's own sampling (StoryCards' RunPaint / logo paints are High), at an invisible alpha
    static readonly SKPaint paint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = new SKColor(255, 255, 255, 1) };

    static double checkMs = double.NegativeInfinity, roundStartMs = double.NegativeInfinity;
    static int step;            // 0..Brands-1 brand pictures, then the frames (waiting for the decode), then idle
    static int frameIdx;
    static bool noFrames, logged;

    /// <summary>Call every frame on the render thread with the live canvas (before the graphics are drawn).</summary>
    public static void Tick(SKCanvas canvas, RaceSnapshot snap, double nowMs, Action<string>? log)
    {
        if (!snap.IsConnected || StoryDirector.Visible) return;
        if (step < 0)
        {
            if (nowMs - roundStartMs < RefreshMs) return;   // idle until the next touch-up round
            step = 0; frameIdx = 0;
        }
        if (step == 0 && frameIdx == 0) roundStartMs = nowMs;
        if (nowMs - checkMs < 30) return;                    // a couple of pictures a frame-pair at most
        checkMs = nowMs;
        try
        {
            if (step < Brands.Length)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var b = CornerBrand.User(Brands[step]);
                if (b != null) canvas.DrawBitmap(b, new SKRect(0, 0, 1, 1), paint);
                if (!logged) log?.Invoke($"pit card warm-up: '{Brands[step]}' {(b != null ? "ready" : "none")} in {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F1} ms");
                step++;
                return;
            }
            if (noFrames) { Done(log); return; }
            if (MichelinRun.Count == 0)
            {
                if (nowMs > 120_000) noFrames = true;           // no frame set on this PC
                return;
            }
            if (!MichelinRun.Prewarm() || MichelinRun.Busy) return;   // the background decode is still filling in
            var frames = MichelinRun.Decoded();                  // one frame's texture per call
            if (frameIdx >= frames.Count)
            {
                if (!logged) log?.Invoke($"pit card warm-up: {frames.Count} running-man frames on the GPU (kept warm, touched every {RefreshMs / 1000:F0} s)");
                Done(log);
                return;
            }
            canvas.DrawImage(frames[frameIdx++], new SKRect(0, 0, 1, 1), paint);
        }
        catch (Exception ex) { log?.Invoke("pit card warm-up: " + ex.Message); Done(log); }
    }

    static void Done(Action<string>? log) { step = -1; logged = true; }
}
