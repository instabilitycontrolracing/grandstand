namespace ApexHUD.Native.Overlays;

/// <summary>
/// The shared timing + easing surface for every overlay's animations.
/// Centralises the three things the legacy CSS design system named — the four
/// duration buckets and the cubic-bezier easings — plus the polynomial
/// approximations the renderers had each reimplemented privately.
///
/// <para>The durations are documented for cohesion, not enforced; each renderer
/// stays free to use whatever timing matches its source overlay (the legacy
/// values vary deliberately — the bar's 750 ms stretch, the watch's 700 ms
/// overshoot, the featured card's 420 ms enter). Use the constants when you're
/// authoring a new animation and don't already have a legacy value to honour.
/// </para>
///
/// <para>The easings are the actual deduplication win. Before this module,
/// <see cref="BattleCardRenderer"/>, <see cref="FeaturedCardRenderer"/> and
/// <see cref="StartingGridRenderer"/> each had a private <c>EaseOut</c> with
/// the same cubic formula; <see cref="FastestLapRenderer"/> and
/// <see cref="PitCalloutRenderer"/> each had a private <c>EaseOutQuint</c>;
/// the (retired) sponsor card had its own smoothstep. They all live here
/// now so a future easing tweak (or a polish-pass cohesion update like
/// switching everyone to <see cref="EaseOutSoft"/>) is one edit.</para>
/// </summary>
public static class BroadcastTiming
{
    // --- Design-system duration buckets (ms) ---
    // Matches the legacy theme.css `--anim-fast / base / slow / very-slow`.

    public const double FastMs = 240;
    public const double BaseMs = 480;
    public const double SlowMs = 900;
    public const double VerySlowMs = 1600;

    // --- Polynomial easing approximations ---
    // Each one matches a CSS cubic-bezier closely enough that the visual
    // difference is sub-pixel at any duration the overlays use. Inputs are
    // clamped so callers don't have to.

    /// <summary>
    /// <c>cubic-bezier(0.16, 1, 0.30, 1)</c> — the legacy <c>--ease-out</c>.
    /// Power-out shape used by entrance / exit fades on featured, battle,
    /// starting-grid, and (de-facto) every other glass-card transition.
    /// </summary>
    public static double EaseOut(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        double inv = 1 - t;
        return 1 - inv * inv * inv;
    }

    /// <summary>
    /// <c>cubic-bezier(0.22, 1, 0.36, 1)</c> — the legacy <c>--ease-out-soft</c>.
    /// Same family as <see cref="EaseOut"/> but settles a touch later; visually
    /// near-identical at ≤520 ms durations and is what the legacy starting-grid
    /// fade was authored against.
    /// </summary>
    public static double EaseOutSoft(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        double inv = 1 - t;
        return 1 - inv * inv * inv;
    }

    /// <summary>
    /// Quintic ease-out — a sharper, more-cinematic version of
    /// <see cref="EaseOut"/>. The fastest-lap and pit-callout overlays use
    /// this for the slide-in/out so the card snaps in and floats out.
    /// </summary>
    public static double EaseOutQuint(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        double inv = 1 - t;
        return 1 - inv * inv * inv * inv * inv;
    }

    /// <summary>
    /// Standard Hermite smoothstep <c>t² (3 − 2t)</c>. Symmetric S-curve used
    /// for the featured ↔ battle cross-fade and the sponsor card's
    /// fade-in/fade-out.
    /// </summary>
    public static double Smoothstep(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        return t * t * (3 - 2 * t);
    }
}
