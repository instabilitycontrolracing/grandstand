using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// V2 (2026-10-27): day-to-night graphics. As the sun goes down in the sim the whole broadcast package eases into a
/// night palette - midnight-navy glass, warm ivory type at ~86 %, amber golds (round 2: clearly visible, see Matrix) - and
/// back at dawn. Driven by the sun's height worked out from iRacing's time of day (SessionTimeOfDay), the track's latitude
/// and the date (summer solstice if unknown); civil dusk is the full night look.
/// <para>Base: one colour grade over every graphic (a single colour-matrix layer while it is dusk or night; nothing at
/// all by day). Elevated ("night glow"): the same grade plus a soft bloom - the bright type and lines get a gentle halo,
/// like broadcast graphics on a night race - and a warmer, sodium-lamp lift in the golds.</para>
/// <para>Cost: nothing in daylight. Dusk / night: one full-screen layer per present (base); elevated adds a blur
/// pass over that layer (measure on the PC: "frame cost" log). Filters are built once per 1/32 step of the fade and
/// cached - no per-frame allocation.</para>
/// </summary>
public sealed class NightPalette : IDisposable
{
    const int Steps = 32;
    readonly SKPaint[] basePaints = new SKPaint[Steps + 1];
    readonly SKPaint[] glowPaints = new SKPaint[Steps + 1];
    float k, target;
    double lastMs = -1;

    /// <summary>The elevated look (bloom + warm golds).</summary>
    public bool Elevated { get; set; }

    /// <summary>0 = day (no grade), 1 = full night.</summary>
    public float K => k;

    /// <summary>Forces the night amount (previews / the control app's test). NaN = follow the sun.</summary>
    public float Override { get; set; } = float.NaN;

    /// <summary>The fade is still moving (keep presenting).</summary>
    public bool Moving => Math.Abs(k - target) > 0.002f;

    /// <summary>Sun elevation in degrees for a local time of day (s), latitude (deg) and day of the year.</summary>
    public static double SunElevation(double timeOfDaySec, double latitudeDeg, int dayOfYear = 172)
    {
        double lat = (double.IsNaN(latitudeDeg) ? 47.95 : latitudeDeg) * Math.PI / 180;
        double decl = 23.44 * Math.PI / 180 * Math.Sin(2 * Math.PI * (284 + dayOfYear) / 365.0);
        double ha = (timeOfDaySec / 3600.0 - 12.0) * 15.0 * Math.PI / 180;
        double s = Math.Sin(lat) * Math.Sin(decl) + Math.Cos(lat) * Math.Cos(decl) * Math.Cos(ha);
        return Math.Asin(Math.Clamp(s, -1, 1)) * 180 / Math.PI;
    }

    /// <summary>Night amount for a sun elevation: 0 above +6 deg (golden hour still day), 1 below -6 deg (civil dusk).</summary>
    public static float NightAmount(double elevationDeg)
    {
        float v = Vp.Clamp01((float)((6.0 - elevationDeg) / 12.0));
        return v * v * (3f - 2f * v);
    }

    /// <summary>Once per tick: ease toward the sun's night amount (about 4 s for a full change, so a jump never pops).</summary>
    public void Tick(double timeOfDaySec, double latitudeDeg, bool connected, double nowMs)
    {
        target = !float.IsNaN(Override) ? Override : connected && timeOfDaySec > 0 ? NightAmount(SunElevation(timeOfDaySec, latitudeDeg)) : 0f;
        double dt = lastMs < 0 ? 16 : Math.Clamp(nowMs - lastMs, 0, 100);
        lastMs = nowMs;
        float step = (float)(dt / 4000.0);
        k = Math.Abs(target - k) <= step ? target : k + Math.Sign(target - k) * step;
    }

    /// <summary>Starts the grade (returns the save count to restore to, or -1 when it is day and nothing was done).</summary>
    public int Begin(SKCanvas c)
    {
        int i = (int)MathF.Round(k * Steps);
        if (i <= 0) return -1;
        var p = Elevated ? (glowPaints[i] ??= MakePaint(i / (float)Steps, true)) : (basePaints[i] ??= MakePaint(i / (float)Steps, false));
        return c.SaveLayer(p);
    }

    public static void End(SKCanvas c, int save) { if (save >= 0) c.RestoreToCount(save); }

    static SKPaint MakePaint(float n, bool glow)
    {
        var p = new SKPaint { ColorFilter = SKColorFilter.CreateColorMatrix(Matrix(n, glow)) };
        if (glow)
        {
            // soft halo: the layer blurred, faded and added under the sharp graphics
            using var fade = SKColorFilter.CreateColorMatrix(new float[]
            {
                1, 0, 0, 0, 0,
                0, 1, 0, 0, 0,
                0, 0, 1, 0, 0,
                0, 0, 0, 0.9f * n, 0,
            });
            using var blur = SKImageFilter.CreateBlur(7f, 7f);
            using var halo = SKImageFilter.CreateColorFilter(fade, blur);
            p.ImageFilter = SKImageFilter.CreateMerge(new[] { halo, null }, null);
        }
        return p;
    }

    /// <summary>
    /// The night grade (round 2, 2026-10-27 - Angelo: "I like the idea but the transition doesn't show up"): the old grade
    /// only darkened the glass a little, and dark glass darkened is still dark glass. Now the package changes CHARACTER at
    /// night, the way night-race broadcasts switch to a night palette: the deep glass turns midnight navy (the blacks are
    /// lifted into blue), the whites settle to a warm ivory at ~86 % so they don't glare on a dark picture, the golds lean
    /// amber (sodium lamps), saturation eases off a little. At full night: black -> rgb(6, 10, 30), white -> rgb(224, 219, 206).
    /// Elevated adds the bloom (see <see cref="MakePaint"/>) and a touch more warmth in the golds.
    /// </summary>
    static float[] Matrix(float n, bool glow)
    {
        float sat = 1f - 0.18f * n;
        float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
        float[,] s =
        {
            { lr + (1 - lr) * sat, lg - lg * sat, lb - lb * sat },
            { lr - lr * sat, lg + (1 - lg) * sat, lb - lb * sat },
            { lr - lr * sat, lg - lg * sat, lb + (1 - lb) * sat },
        };
        // gains (whites -> warm ivory 86 %) and the navy lift on the blacks (Skia's matrix offsets are 0..1)
        float gr = 1f - 0.12f * n, gg = 1f - 0.14f * n, gb = 1f - 0.31f * n;
        float or = 0.024f * n, og = 0.04f * n, ob = 0.118f * n;
        if (glow) { gr += 0.03f * n; gg += 0.01f * n; }
        float[] row(int r, float gain, float off) => new[] { s[r, 0] * gain, s[r, 1] * gain, s[r, 2] * gain, 0f, off };
        var m = new List<float>(20);
        m.AddRange(row(0, gr, or)); m.AddRange(row(1, gg, og)); m.AddRange(row(2, gb, ob));
        m.AddRange(new[] { 0f, 0f, 0f, 1f, 0f });
        return m.ToArray();
    }

    public void Dispose()
    {
        foreach (var p in basePaints) { p?.ImageFilter?.Dispose(); p?.ColorFilter?.Dispose(); p?.Dispose(); }
        foreach (var p in glowPaints) { p?.ImageFilter?.Dispose(); p?.ColorFilter?.Dispose(); p?.Dispose(); }
    }
}
