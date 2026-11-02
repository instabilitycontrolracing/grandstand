namespace ApexHUD.Native.Overlays;

/// <summary>The transform of the watch at a moment in its entrance animation.</summary>
/// <param name="Scale">Uniform scale about the watch centre.</param>
/// <param name="RotationDeg">Clockwise rotation in degrees.</param>
/// <param name="Opacity">0..1 fade.</param>
/// <param name="Blur">Gaussian blur in legacy watch-px (0..2); callers scale to device px.</param>
/// <param name="IsComplete">True once the animation has settled.</param>
public readonly record struct WatchArriveState(
    float Scale, float RotationDeg, float Opacity, float Blur, bool IsComplete);

/// <summary>
/// The legacy <c>v46-watch-arrive</c> entrance: a 700 ms settling overshoot the
/// watch plays on launch (scale 0.36→1.085→0.965→1, slight rotate, blur 2→0,
/// fade in over the first 35%). Each keyframe segment is eased by
/// <c>cubic-bezier(0.34, 1.4, 0.64, 1)</c>, matching the CSS animation exactly.
/// </summary>
public static class WatchArrive
{
    public const double DurationMs = 700;

    static readonly CubicBezier Ease = new(0.34, 1.4, 0.64, 1.0);

    // Per-property keyframes (position 0..1, value). A property is interpolated
    // only between the keyframes that declare it — mirroring CSS @keyframes.
    static readonly (double pos, double val)[] ScaleKeys =
        { (0, 0.36), (0.62, 1.085), (0.80, 0.965), (1.0, 1.0) };
    static readonly (double pos, double val)[] RotateKeys =
        { (0, -14), (0.62, 3), (0.80, -1), (1.0, 0) };
    static readonly (double pos, double val)[] OpacityKeys =
        { (0, 0), (0.35, 1), (1.0, 1) };
    static readonly (double pos, double val)[] BlurKeys =
        { (0, 2), (0.35, 0), (1.0, 0) };

    public static WatchArriveState Evaluate(double elapsedMs)
    {
        double p = Math.Clamp(elapsedMs / DurationMs, 0, 1);
        return new WatchArriveState(
            (float)Sample(ScaleKeys, p),
            (float)Sample(RotateKeys, p),
            (float)Sample(OpacityKeys, p),
            (float)Sample(BlurKeys, p),
            p >= 1.0);
    }

    static double Sample((double pos, double val)[] keys, double p)
    {
        if (p <= keys[0].pos) return keys[0].val;
        for (int i = 1; i < keys.Length; i++)
        {
            if (p <= keys[i].pos)
            {
                var (p0, v0) = keys[i - 1];
                var (p1, v1) = keys[i];
                double eased = Ease.Solve((p - p0) / (p1 - p0));
                return v0 + (v1 - v0) * eased;
            }
        }
        return keys[^1].val;
    }
}

/// <summary>
/// A CSS-style cubic-bezier timing function through (0,0) and (1,1) with the
/// two control points supplied. <see cref="Solve"/> maps an input fraction to
/// the eased output (which may exceed 1 for overshoot control points).
/// </summary>
public sealed class CubicBezier
{
    readonly double ax, bx, cx, ay, by, cy;

    public CubicBezier(double x1, double y1, double x2, double y2)
    {
        cx = 3 * x1;
        bx = 3 * (x2 - x1) - cx;
        ax = 1 - cx - bx;
        cy = 3 * y1;
        by = 3 * (y2 - y1) - cy;
        ay = 1 - cy - by;
    }

    public double Solve(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        return SampleY(SolveForT(x));
    }

    double SampleX(double t) => ((ax * t + bx) * t + cx) * t;
    double SampleY(double t) => ((ay * t + by) * t + cy) * t;
    double SampleDerivX(double t) => (3 * ax * t + 2 * bx) * t + cx;

    double SolveForT(double x)
    {
        // Newton-Raphson, then bisection fallback (the browser approach).
        double t = x;
        for (int i = 0; i < 8; i++)
        {
            double err = SampleX(t) - x;
            if (Math.Abs(err) < 1e-6) return t;
            double d = SampleDerivX(t);
            if (Math.Abs(d) < 1e-6) break;
            t -= err / d;
        }

        double lo = 0, hi = 1;
        t = x;
        while (lo < hi)
        {
            double xt = SampleX(t);
            if (Math.Abs(xt - x) < 1e-6) return t;
            if (x > xt) lo = t; else hi = t;
            t = (lo + hi) / 2;
        }
        return t;
    }
}
