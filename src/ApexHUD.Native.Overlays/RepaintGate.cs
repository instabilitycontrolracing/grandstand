namespace ApexHUD.Native.Overlays;

/// <summary>
/// Per-window predicate that decides whether to <c>InvalidateVisual</c> this
/// frame. Returns true when the window visibility gate is open OR while a
/// just-flipped visibility transition is still mid-fade. Once the window is
/// hidden and the fade has elapsed it goes quiet - no further WPF repaints
/// until the gate opens again.
///
/// <para>It also enforces a frame-rate cap: even a fully-visible window repaints
/// at most <c>maxFps</c> times a second instead of on every monitor refresh.
/// The transition clips are 30 fps and standings change only a few times a
/// second, so painting every 144 Hz refresh redrew identical pixels 4-5x and
/// starved the splash decode path (the slow/clunky transitions). Default 30 fps;
/// pass a higher cap for genuinely fast content.</para>
/// </summary>
public struct RepaintGate
{
    public const double FadeOverhangMs = 60.0;
    public const double DefaultMaxFps = 30.0;

    bool wasVisible;
    bool armed;
    double flipAtMs;
    double lastPaintMs;
    readonly double fadeMs;
    readonly double minIntervalMs;

    public RepaintGate(double fadeMs) : this(fadeMs, DefaultMaxFps) { }

    public RepaintGate(double fadeMs, double maxFps)
    {
        this.fadeMs = fadeMs + FadeOverhangMs;
        this.minIntervalMs = maxFps > 0 ? 1000.0 / maxFps : 0;
        wasVisible = false;
        armed = false;
        flipAtMs = 0;
        lastPaintMs = double.NegativeInfinity;
    }

    /// <summary>True while the window should repaint this frame.</summary>
    public bool ShouldPaint(bool visible, double nowMs)
    {
        bool want;
        if (!armed || visible != wasVisible)
        {
            wasVisible = visible;
            flipAtMs = nowMs;
            armed = true;
            want = true;
        }
        else if (visible)
        {
            want = true;
        }
        else
        {
            want = (nowMs - flipAtMs) < fadeMs;
        }

        if (!want) return false;

        // Frame-rate cap: throttle even a visible window so we never repaint
        // identical pixels every monitor refresh.
        if (nowMs - lastPaintMs < minIntervalMs) return false;
        lastPaintMs = nowMs;
        return true;
    }
}

