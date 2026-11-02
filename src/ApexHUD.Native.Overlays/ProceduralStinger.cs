using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// The built-in animated class transition (2026-10-24, public build): a TV "roller" drawn live with
/// SkiaSharp - zero file assets - that takes the place of the owner's IMSA footage frames in the timing
/// tower's scene-change splash. Same role and timing as the footage: SceneDirector starts it at the same
/// trigger points, it lasts <see cref="DurationMs"/> (the length of the footage it replaces: overall 2.8 s,
/// classes 5.6 s), the tower lane is fully covered between the wipe-in and the wipe-out (the scene swap at
/// the midpoint stays hidden), and the splash renderer's wipe clip + glowing edge still frame it.
///
/// <para><b>Look.</b> Deep glass base tinted by the class colour (ClassColors: GT3/GTD green, LMP2 blue,
/// GTP black - lit in silver, OVERALL gold), diagonal light sweeps and motion streaks racing across the
/// lane, a chevron flythrough that rushes at the camera on the way in and idles through the hold, a giant
/// outlined class name scrolling behind, and a glass panel that punches in with a flash carrying the class
/// logo (or the class name in big type), the iRacing wordmark and the series logo. Edge rails with moving
/// ticks, a glint across the glass, scanlines, film grain and a vignette finish it. On the way out the
/// streaks and chevrons accelerate into the wipe.</para>
///
/// <para><b>Cost (2026-10-30, Angelo: "class transitions choppy", ~42 ms a frame at 4K).</b> Measured with
/// tools/stinger_bench on Skia's OpenGL path: the time went into the giant outlined ghost word and the chevrons
/// (big concave paths the graphics card has to re-rasterise into a coverage mask every frame), the blurred name
/// glow (an offscreen blur every frame) and five full-lane fills. Now the look is the same but:
/// the parts that never change during a splash are drawn once, on a background thread, into pictures at the
/// screen's own pixel grid (<see cref="Cache"/>): the base + class bloom (two pictures blended by the bloom's
/// level in one pass), the scanlines + vignette (one picture), the ghost word (one picture, drawn at each scroll
/// position), the blurred name glow, and the settled glass panel in its draw-order layers (the moving glint is
/// still drawn live between them). The chevrons are drawn as convex pieces (two arms + the six edge strips with
/// the same miter corners), which the card draws directly without a mask. Until the background pictures are
/// ready (the first few frames of a splash) everything is drawn live exactly as before.</para>
/// </summary>
public sealed class ProceduralStinger : IDisposable
{
    public const double OverallMs = 2800, ClassMs = 5600;

    /// <summary>Total splash length for a scene (matches the footage clips it replaces).</summary>
    public static double DurationMs(Scene? s) => s == null || s.Focus == SceneFocus.Overall ? OverallMs : ClassMs;

    // timeline (ms from the splash start)
    const double PunchMs = 430, NameMs = 520, KickerMs = 650, LineMs = 620, LogosMs = 720, GlintStartMs = 1250, GlintEveryMs = 1900, ExitLeadMs = 700;
    /// <summary>From here until the exit every panel animation has settled exactly (all its curves are clamped at 1):
    /// series logo Smooth((t-840)/300) is the last to finish.</summary>
    const double HoldStartMs = 1140;
    const float Angle = -62f;                 // streak / sweep direction (degrees, steep diagonal)

    const int StreakCount = 34, ChevronCount = 7, BandCount = 3, TickSpacing = 22;
    readonly float[] sV = new float[StreakCount], sLen = new float[StreakCount], sSpd = new float[StreakCount],
                     sTh = new float[StreakCount], sOff = new float[StreakCount], sA = new float[StreakCount];
    readonly bool[] sWhite = new bool[StreakCount];
    readonly float[] bHalf = { 70f, 130f, 48f }, bOff = { 0f, 0.37f, 0.71f }, bSpd = { 1f, 0.62f, 1.35f }, bA = { 0.85f, 0.55f, 0.7f };

    // reused paints (colours/alphas are set per frame; shaders only on Ensure)
    readonly SKPaint basePaint = new() { IsAntialias = false };
    readonly SKPaint bloomPaint = new() { IsAntialias = false, BlendMode = SKBlendMode.Plus };
    readonly SKPaint vignettePaint = new() { IsAntialias = false };
    readonly SKPaint bandPaint = new() { IsAntialias = false, BlendMode = SKBlendMode.Plus };
    readonly SKPaint streakPaint = new() { IsAntialias = true, BlendMode = SKBlendMode.Plus };
    readonly SKPaint streakWhitePaint = new() { IsAntialias = true, BlendMode = SKBlendMode.Plus };
    readonly SKPaint chevronPaint = new() { IsAntialias = true, BlendMode = SKBlendMode.Plus };
    readonly SKPaint chevronEdgePaint = new() { IsAntialias = true, BlendMode = SKBlendMode.Plus };   // fills the edge strips
    readonly SKPaint flashPaint = new() { IsAntialias = false, BlendMode = SKBlendMode.Plus };
    readonly SKPaint burstPaint = new() { IsAntialias = true, BlendMode = SKBlendMode.Plus };
    readonly SKPaint ghostFillPaint = new() { IsAntialias = true };
    readonly SKPaint ghostEdgePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f };
    readonly SKPaint railPaint = new() { IsAntialias = true, StrokeWidth = 1.2f };
    readonly SKPaint tickPaint = new() { IsAntialias = true, StrokeWidth = 1.4f };
    readonly SKPaint edgeBarPaint = new() { IsAntialias = false };
    readonly SKPaint grainPaint = new() { IsAntialias = false, BlendMode = SKBlendMode.Overlay };
    readonly SKPaint scanPaint = new() { IsAntialias = false };
    // drawing the cached pictures (alpha / blend set per draw)
    readonly SKPaint imgPaint = new() { IsAntialias = false, FilterQuality = SKFilterQuality.None };
    readonly SKPaint imgSmoothPaint = new() { IsAntialias = false, FilterQuality = SKFilterQuality.Low };
    readonly SKPaint lerpPaint = new() { IsAntialias = false };

    // the chevron as convex pieces (unit coords, pointing up; arms reach x = -1..1): two arms + six edge strips
    static readonly SKPoint[] ChevronPts = { new(-1f, 0.42f), new(0f, -0.42f), new(1f, 0.42f), new(1f, 0.72f), new(0f, -0.12f), new(-1f, 0.72f) };
    readonly SKPath armL = new(), armR = new();
    readonly SKPath[] edgeStrip = { new(), new(), new(), new(), new(), new() };
    readonly SKPoint[] edgeOut = new SKPoint[6], edgeIn = new SKPoint[6];

    readonly PanelKit kit = new();
    SKPath? ghost; SKRect ghostBounds;

    // what the cached shaders/blobs were built for
    float builtW = -1, builtH = -1; SKColor builtColor; string? builtLabel; int builtFlags = -1;
    SKColor light;

    // the label is derived per scene object (no per-frame string work)
    Scene? labelScene; string labelText = "";

    static SKImage? grainImg, scanImg;
    static readonly object imgGate = new();

    public ProceduralStinger()
    {
        var rnd = new Random(1907);
        for (int i = 0; i < StreakCount; i++)
        {
            sV[i] = (float)(rnd.NextDouble() * 2 - 1);                 // across the lane (-1..1 of the half diagonal)
            sLen[i] = 90f + (float)rnd.NextDouble() * 360f;
            sSpd[i] = 0.55f + (float)rnd.NextDouble() * 0.9f;
            sTh[i] = i % 7 == 0 ? 3.2f + (float)rnd.NextDouble() * 2.5f : 0.8f + (float)rnd.NextDouble() * 1.8f;
            sOff[i] = (float)rnd.NextDouble();
            sA[i] = 0.35f + (float)rnd.NextDouble() * 0.65f;
            sWhite[i] = i % 3 == 0;
        }
        var p = ChevronPts;
        armL.MoveTo(p[5]); armL.LineTo(p[0]); armL.LineTo(p[1]); armL.LineTo(p[4]); armL.Close();
        armR.MoveTo(p[1]); armR.LineTo(p[2]); armR.LineTo(p[3]); armR.LineTo(p[4]); armR.Close();
        EnsureImages();
    }

    // ------------------------------------------------------------------ timing curves (stateless, deterministic)

    static double Smooth(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
    static double OutCubic(double x) { x = Math.Clamp(x, 0, 1); double k = 1 - x; return 1 - k * k * k; }
    static double OutBack(double x) { x = Math.Clamp(x, 0, 1); const double c1 = 1.70158, c3 = c1 + 1; return 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2); }

    /// <summary>Distance travelled by something cruising at <paramref name="slow"/> that starts with a burst
    /// (+<paramref name="burst"/>, decaying over tau) and accelerates again into the exit (+<paramref name="exit"/>).
    /// Units: per ms. Integral of v(t) = slow + burst e^(-t/tau) + exit e^((t-D)/tau2).</summary>
    static double Travel(double t, double total, double slow, double burst, double exit, double tau = 420, double tau2 = 260) =>
        slow * t + burst * tau * (1 - Math.Exp(-t / tau)) + exit * tau2 * (Math.Exp((t - total) / tau2) - Math.Exp(-total / tau2));

    /// <summary>0..1 speed feel (1 = burst/exit, 0 = cruising) for streak length/brightness.</summary>
    static double Energy(double t, double total) => Math.Clamp(Math.Exp(-t / 520) + Math.Exp((t - total) / 300), 0, 1);

    static float Frac(double x) => (float)(x - Math.Floor(x));

    // ------------------------------------------------------------------ draw

    /// <summary>Draws the stinger into <paramref name="lane"/> (1080p design units; the host scales for 4K).</summary>
    /// <param name="t">ms since the splash started.</param>
    /// <param name="total">total splash length (ms).</param>
    public void Draw(SKCanvas c, SKRect lane, Scene scene, SKColor classColor, double t, double total,
                     SKBitmap? wordmark, SKBitmap? series, SKBitmap? classLogo)
    {
        float W = lane.Width, H = lane.Height;
        if (W < 8 || H < 8) return;
        if (t < 0) t = 0;
        if (!ReferenceEquals(scene, labelScene)) { labelScene = scene; labelText = LabelFor(scene); }
        string label = labelText;
        Ensure(W, H, classColor, label, classLogo != null, wordmark != null, series != null);

        double energy = Energy(t, total);
        double exitK = Smooth((t - (total - ExitLeadMs)) / ExitLeadMs);

        int save = c.Save();
        c.Translate(lane.Left, lane.Top);
        c.ClipRect(new SKRect(0, 0, W, H));
        var cache = CacheFor(c.TotalMatrix, W, H, classColor, label, wordmark, series, classLogo);

        // 1. base + class bloom
        byte bloomA = A(150 + 35 * Math.Sin(t / 900.0) + 60 * energy);
        if (cache != null) DrawBase(c, cache, bloomA);
        else
        {
            c.DrawRect(0, 0, W, H, basePaint);
            bloomPaint.Color = SKColors.White.WithAlpha(bloomA);
            c.DrawRect(0, 0, W, H, bloomPaint);
        }

        // 2. giant outlined class name scrolling up behind everything
        DrawGhost(c, W, H, t, total, cache);

        // 3. diagonal light sweeps + motion streaks (rotated frame)
        float diag = MathF.Sqrt(W * W + H * H), half = diag / 2f;
        c.Save();
        c.Translate(W / 2f, H / 2f);
        c.RotateDegrees(Angle);
        for (int i = 0; i < BandCount; i++)
        {
            double travel = Travel(t, total, 0.00012, 0.0016, 0.0012) * bSpd[i] + bOff[i];
            float v = (Frac(travel) * 2f - 1f) * (half + bHalf[i]);
            float a = (float)(bA[i] * (0.35 + 0.65 * energy) * (0.7 + 0.3 * Math.Sin(t / 700.0 + i)));
            bandPaint.Color = SKColors.White.WithAlpha((byte)Math.Clamp(a * 255, 0, 255));
            c.Save();
            c.Translate(0, v);
            c.Scale(half + 40, bHalf[i]);
            c.DrawRect(-1, -1, 2, 2, bandPaint);
            c.Restore();
        }
        double sTravel = Travel(t, total, 0.32, 3.4, 2.8);
        float span = diag + 2 * 450f;
        float lenK = (float)(0.6 + 0.7 * energy), aK = (float)(0.62 + 0.38 * energy);
        for (int i = 0; i < StreakCount; i++)
        {
            float u = Frac((sTravel * sSpd[i]) / span + sOff[i]) * span - half - 450f;
            float len = sLen[i] * lenK;
            var p = sWhite[i] ? streakWhitePaint : streakPaint;
            p.Color = SKColors.White.WithAlpha((byte)Math.Clamp(255 * sA[i] * aK, 0, 255));
            c.Save();
            c.Translate(u, sV[i] * half);
            c.Scale(len, sTh[i]);
            c.DrawRect(0, -0.5f, 1, 1, p);
            c.Restore();
        }
        c.Restore();

        // 4. chevron flythrough (vanishing point just above the panel)
        DrawChevrons(c, W, H, t, total, exitK);

        // 5. edge rails with moving ticks + class bars top/bottom
        DrawRails(c, W, H, t, total);

        // 6. punch-in flash + burst
        if (t >= PunchMs - 40 && t < PunchMs + 420)
        {
            double k = (t - PunchMs) / 220.0;
            float fa = (float)(k < 0 ? (1 + k * 5.5) : Math.Exp(-k * 2.2));
            flashPaint.Color = SKColors.White.WithAlpha((byte)Math.Clamp(110 * fa, 0, 255));
            c.DrawRect(0, 0, W, H, flashPaint);
            float r = (float)(40 + 520 * OutCubic((t - PunchMs + 40) / 420.0));
            burstPaint.Color = SKColors.White.WithAlpha((byte)Math.Clamp(200 * (1 - (t - PunchMs + 40) / 460.0), 0, 255));
            c.Save();
            c.Translate(W / 2f, H * PanelY);
            c.Scale(r, r * 0.8f);
            c.DrawCircle(0, 0, 1, burstPaint);
            c.Restore();
        }

        // 7. the glass panel with the class, wordmark and series
        bool hold = t >= HoldStartMs && t < total - ExitLeadMs;
        if (hold && cache?.Panel != null) DrawPanelCached(c, W, H, t, cache);
        else kit.Draw(c, W, H, t, exitK, wordmark, series, classLogo, PanelKit.All, cache?.Images, imgSmoothPaint);

        // 8. finish: scanlines, grain, vignette
        int frame = (int)(t / 33.0);
        uint hsh = (uint)frame * 2654435761u;
        float jx = (hsh & 127), jy = ((hsh >> 8) & 127);
        if (cache != null)
        {
            // the scanlines are a darkening multiply like the grain on the dark lane, so they go on with the vignette
            // in one cached pass after the grain (identical on everything but the brightest pixels: within a level or two)
            c.Save(); c.Translate(-jx, -jy); c.DrawRect(jx, jy, W, H, grainPaint); c.Restore();
            DrawDev(c, cache.Mask, cache.LaneX, cache.LaneY, imgPaint, 255, SKBlendMode.SrcOver);
        }
        else
        {
            c.DrawRect(0, 0, W, H, scanPaint);
            c.Save();
            c.Translate(-jx, -jy);
            c.DrawRect(jx, jy, W, H, grainPaint);
            c.Restore();
            c.DrawRect(0, 0, W, H, vignettePaint);
        }

        c.RestoreToCount(save);
    }

    static string LabelFor(Scene scene) =>
        scene.Focus == SceneFocus.Overall ? "OVERALL"
        : scene.Focus == SceneFocus.Pits ? "PIT STOPS"      // the class logo still shows which class
        : ClassLogo.DisplayName(scene.ClassName ?? scene.Label);

    /// <summary>Starts drawing the still parts of the NEXT splash in the background while the board holds, so the
    /// roller is at full speed from its first frame. Same arguments as <see cref="Draw"/> (the canvas as the splash
    /// will see it); cheap to call every frame - it only compares a key until the next scene changes.</summary>
    public void Prepare(SKCanvas c, SKRect lane, Scene scene, SKColor classColor, SKBitmap? wordmark, SKBitmap? series, SKBitmap? classLogo)
    {
        float W = lane.Width, H = lane.Height;
        if (W < 8 || H < 8) return;
        if (scene != prepScene) { prepScene = scene; prepLabel = LabelFor(scene); }   // value compare: the director rebuilds its scenes every tick
        var m = c.TotalMatrix.PreConcat(SKMatrix.CreateTranslation(lane.Left, lane.Top));
        CacheFor(m, W, H, classColor, prepLabel, wordmark, series, classLogo);
    }
    Scene? prepScene; string prepLabel = "";

    /// <summary>True when no background build is running (for tools/stinger_bench).</summary>
    public bool Idle => building == null || building.IsCompleted;

    const float PanelY = 0.47f, PadTop = 30f, PadBottom = 30f, WordmarkH = 26f, SeriesH = 52f;
    static byte A(double v) => (byte)Math.Clamp(v, 0, 255);

    void DrawGhost(SKCanvas c, float W, float H, double t, double total, Cache? cache)
    {
        var gb = cache?.GhostImg != null ? cache.GhostBounds : ghostBounds;
        if ((cache?.GhostImg == null && ghost == null) || gb.Width <= 1) return;
        // rotated -90: the word reads bottom-to-top and scrolls upward
        float step = gb.Width + 70f;
        float off = Frac(Travel(t, total, 0.045, 0.35, 0.3) / step) * step;
        float a = (float)Smooth(t / 500.0);
        float x = W * 0.5f - gb.Top * 0.5f;   // glyphs extend left of x once rotated: centre them
        if (cache?.GhostImg != null)
        {
            // the word, outline and all, is one picture drawn at each scroll position (the fill/edge alphas are baked in)
            var m = c.TotalMatrix;
            for (float y = H + off; y > -step; y -= step)
            {
                var d = m.MapPoint(x, y);
                DrawDevF(c, cache.GhostImg, d.X + cache.GhostOx, d.Y + cache.GhostOy, imgSmoothPaint, A(255 * a));
            }
            return;
        }
        ghostFillPaint.Color = light.WithAlpha(A(26 * a));
        ghostEdgePaint.Color = SKColors.White.WithAlpha(A(40 * a));
        for (float y = H + off; y > -step; y -= step)
        {
            c.Save();
            c.Translate(x, y);
            c.RotateDegrees(-90);
            c.Translate(-gb.Left, 0);
            c.DrawPath(ghost!, ghostFillPaint);
            c.DrawPath(ghost!, ghostEdgePaint);
            c.Restore();
        }
    }

    void DrawChevrons(SKCanvas c, float W, float H, double t, double total, double exitK)
    {
        double phase = 0.10 * t / 1000.0 + 1.9 * (1 - Math.Exp(-t / 520.0)) + 1.2 * (Math.Exp((t - total) / 300.0) - Math.Exp(-total / 300.0));
        double intensity = 0.16 + 0.84 * Math.Exp(-Math.Max(0, t - 700) / 380.0) * Smooth(t / 120.0) + 0.6 * exitK;
        float vx = W / 2f, vy = H * (PanelY - 0.04f);
        float baseS = W * 0.10f;
        for (int i = 0; i < ChevronCount; i++)
        {
            float z = Frac(i / (double)ChevronCount - phase);
            if (z < 0.035f) continue;
            float s = baseS / z;
            float far = Math.Clamp((1f - z) / 0.35f, 0f, 1f);
            float near = Math.Clamp((W * 2.1f - s) / (W * 1.1f), 0f, 1f);
            float a = (float)(far * near * intensity);
            if (a <= 0.01f) continue;
            chevronPaint.Color = SKColors.White.WithAlpha((byte)Math.Clamp(a * 70, 0, 255));
            chevronEdgePaint.Color = SKColors.White.WithAlpha((byte)Math.Clamp(a * 210, 0, 255));
            BuildEdgeStrips(0.75f / s);   // the ~1.5 px edge stroke, whatever the scale
            c.Save();
            c.Translate(vx, vy - s * 0.18f);
            c.Scale(s, s);
            // same pixels as filling + stroking the chevron outline (all Plus: the pieces' shared edges add up exactly)
            c.DrawPath(armL, chevronPaint);
            c.DrawPath(armR, chevronPaint);
            for (int e = 0; e < 6; e++) c.DrawPath(edgeStrip[e], chevronEdgePaint);
            c.Restore();
        }
    }

    /// <summary>The stroke of the closed chevron outline (miter joins - every corner is within the default
    /// miter limit) as six convex strips, one per side, meeting on the miter diagonals.</summary>
    void BuildEdgeStrips(float hw)
    {
        var p = ChevronPts;
        for (int i = 0; i < 6; i++)
        {
            var a = p[(i + 5) % 6]; var b = p[i]; var n = p[(i + 1) % 6];
            var n1 = LeftNormal(b.X - a.X, b.Y - a.Y);
            var n2 = LeftNormal(n.X - b.X, n.Y - b.Y);
            float mx = n1.X + n2.X, my = n1.Y + n2.Y;
            float ml = MathF.Sqrt(mx * mx + my * my);
            mx /= ml; my /= ml;
            float len = hw / (mx * n1.X + my * n1.Y);
            edgeOut[i] = new SKPoint(b.X + mx * len, b.Y + my * len);
            edgeIn[i] = new SKPoint(b.X - mx * len, b.Y - my * len);
        }
        for (int i = 0; i < 6; i++)
        {
            int j = (i + 1) % 6;
            var s = edgeStrip[i];
            s.Rewind();
            s.MoveTo(edgeOut[i]); s.LineTo(edgeOut[j]); s.LineTo(edgeIn[j]); s.LineTo(edgeIn[i]); s.Close();
        }
    }

    static SKPoint LeftNormal(float dx, float dy) { float l = MathF.Sqrt(dx * dx + dy * dy); return new SKPoint(dy / l, -dx / l); }

    void DrawRails(SKCanvas c, float W, float H, double t, double total)
    {
        float a = (float)Smooth(t / 300.0);
        railPaint.Color = light.WithAlpha(A(90 * a));
        c.DrawLine(7, 0, 7, H, railPaint);
        c.DrawLine(W - 7, 0, W - 7, H, railPaint);
        float off = Frac(Travel(t, total, 0.06, 0.9, 0.8) / TickSpacing) * TickSpacing;
        tickPaint.Color = SKColors.White.WithAlpha(A(120 * a));
        int n = 0;
        for (float y = off - TickSpacing; y < H + TickSpacing; y += TickSpacing, n++)
        {
            float len = (n % 5 == 0) ? 12f : 5f;
            c.DrawLine(7, y, 7 + len, y, tickPaint);
            c.DrawLine(W - 7, H - y, W - 7 - len, H - y, tickPaint);
        }
        edgeBarPaint.Color = light.WithAlpha(A(230 * a));
        c.DrawRect(0, 0, W, 4, edgeBarPaint);
        c.DrawRect(0, H - 4, W, 4, edgeBarPaint);
    }

    /// <summary>The settled panel (hold): its cached layers in draw order, with the live glint between the glass and
    /// the rest exactly where the live draw puts it.</summary>
    void DrawPanelCached(SKCanvas c, float W, float H, double t, Cache cache)
    {
        var L = cache.Panel!;
        DrawDev(c, L[0].Img, L[0].X, L[0].Y, imgPaint, 255, SKBlendMode.SrcOver);
        if (t > GlintStartMs && ((t - GlintStartMs) % GlintEveryMs) / 650.0 < 1)
        {
            c.Save();
            c.Translate(W / 2f, H * PanelY);
            kit.Draw(c, W, H, t, 0, null, null, null, PanelKit.Glint, null, imgSmoothPaint);
            c.Restore();
        }
        for (int i = 1; i < L.Length; i++)
            if (L[i].Img != null) DrawDev(c, L[i].Img, L[i].X, L[i].Y, imgPaint, 255, L[i].Blend);
    }

    // ------------------------------------------------------------------ cached pictures

    void DrawBase(SKCanvas c, Cache k, byte bloomA)
    {
        // base + bloom * a  ==  lerp(base, base + bloom, a): one pass over two pictures (Plus never clips on this dark base)
        var sh = k.Lerp[bloomA] ??= SKShader.CreateLerp(bloomA / 255f, k.Base0Shader, k.Base1Shader);
        lerpPaint.Shader = sh;
        c.Save();
        c.ResetMatrix();
        c.DrawRect(k.LaneX, k.LaneY, k.Base0.Width, k.Base0.Height, lerpPaint);
        c.Restore();
        lerpPaint.Shader = null;
    }

    /// <summary>Draws a screen-grid picture at its screen pixel position (no resampling).</summary>
    static void DrawDev(SKCanvas c, SKImage? img, int x, int y, SKPaint p, byte alpha, SKBlendMode blend)
    {
        if (img == null) return;
        p.Color = SKColors.White.WithAlpha(alpha);
        p.BlendMode = blend;
        c.Save();
        c.ResetMatrix();
        c.DrawImage(img, x, y, p);
        c.Restore();
        p.BlendMode = SKBlendMode.SrcOver;
    }

    static void DrawDevF(SKCanvas c, SKImage img, float x, float y, SKPaint p, byte alpha)
    {
        p.Color = SKColors.White.WithAlpha(alpha);
        c.Save();
        c.ResetMatrix();
        c.DrawImage(img, x, y, p);
        c.Restore();
    }

    /// <summary>Everything about one splash that does not move, drawn at the screen's pixel grid.</summary>
    sealed class Cache : IDisposable
    {
        public CacheKey Key;
        public int LaneX, LaneY;
        public SKImage Base0 = null!, Base1 = null!, Mask = null!;
        public SKShader Base0Shader = null!, Base1Shader = null!;
        public readonly SKShader?[] Lerp = new SKShader?[256];
        public SKImage? GhostImg; public SKRect GhostBounds; public float GhostOx, GhostOy;
        public PanelImages? Images;
        public Layer[]? Panel;
        public void Dispose()
        {
            foreach (var s in Lerp) s?.Dispose();
            Base0Shader?.Dispose(); Base1Shader?.Dispose();
            Base0?.Dispose(); Base1?.Dispose(); Mask?.Dispose(); GhostImg?.Dispose(); Images?.Dispose();
            if (Panel != null) foreach (var l in Panel) l.Img?.Dispose();
        }
    }

    readonly record struct Layer(SKImage? Img, int X, int Y, SKBlendMode Blend);

    /// <summary>Pictures of the panel's costly pieces in its own coordinates, used while it zooms in and out: the
    /// blurred shadow, the blurred name glow and the big name (drawn twice as sharp, so the zoom stays crisp).</summary>
    sealed class PanelImages : IDisposable
    {
        public SKImage? Shadow, Glow, Name;
        public SKRect ShadowRect, GlowRect, NameRect;
        public void Dispose() { Shadow?.Dispose(); Glow?.Dispose(); Name?.Dispose(); }
    }

    /// <summary>What a cache was drawn for (compared every frame: a value compare, no allocation).</summary>
    readonly record struct CacheKey(float Sx, float Sy, float Tx, float Ty, float W, float H, SKColor Color, string Label,
                                    SKBitmap? Wordmark, SKBitmap? Series, SKBitmap? ClassLogo);

    sealed record Spec(CacheKey Key, SKMatrix M);

    Cache? cache;
    Task<Cache?>? building; CacheKey buildingKey;
    Spec? pending;
    bool cacheBroken;

    Cache? CacheFor(SKMatrix m, float W, float H, SKColor classColor, string label, SKBitmap? wordmark, SKBitmap? series, SKBitmap? classLogo)
    {
        if (cacheBroken) return null;
        // the pictures sit on the screen's pixel grid: only for a plain scale + move (always the case in the host)
        if (m.SkewX != 0 || m.SkewY != 0 || m.Persp0 != 0 || m.Persp1 != 0 || m.ScaleX <= 0 || m.ScaleY <= 0) return null;
        var key = new CacheKey(m.ScaleX, m.ScaleY, m.TransX, m.TransY, W, H, classColor, label, wordmark, series, classLogo);
        if (cache != null && cache.Key == key) return cache;
        if (building != null)
        {
            if (!building.IsCompleted) { if (buildingKey != key && pending?.Key != key) pending = new Spec(key, m); return null; }
            Cache? done = null;
            try { done = building.Result; } catch (Exception ex) { cacheBroken = true; OverlayLog.Write("class transition cache: " + ex.GetBaseException().Message); }
            building = null;
            if (done != null)
            {
                if (done.Key == key) { cache?.Dispose(); cache = done; return cache; }
                done.Dispose();
            }
            if (cacheBroken) return null;
        }
        var spec = pending != null && pending.Key == key ? pending : new Spec(key, m);
        pending = null;
        buildingKey = key;
        building = Task.Run(() => Build(spec));
        return null;
    }

    /// <summary>Background thread: draws every still part of the splash for <paramref name="s"/> with its own paints.</summary>
    static Cache? Build(Spec s)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var m = s.M;
        var q = s.Key;
        float W = q.W, H = q.H;
        var light = CarArt.LightColor(q.Color);
        var k = new Cache { Key = s.Key };
        try
        {
            // lane on the screen grid
            var dev = m.MapRect(new SKRect(0, 0, W, H));
            int lx = (int)Math.Floor(dev.Left), ly = (int)Math.Floor(dev.Top);
            int lw = (int)Math.Ceiling(dev.Right) - lx, lh = (int)Math.Ceiling(dev.Bottom) - ly;
            k.LaneX = lx; k.LaneY = ly;
            var local = SKMatrix.CreateTranslation(-lx, -ly).PreConcat(m);
            var (baseSh, bloomSh, vigSh) = BackgroundShaders(W, H, q.Color, light);
            using (baseSh) using (bloomSh) using (vigSh)
            using (var bp = new SKPaint { Shader = baseSh })
            using (var blp = new SKPaint { Shader = bloomSh, BlendMode = SKBlendMode.Plus, Color = SKColors.White })
            using (var vp = new SKPaint { Shader = vigSh })
            {
                k.Base0 = Render(lw, lh, local, c => c.DrawRect(0, 0, W, H, bp));
                k.Base1 = Render(lw, lh, local, c => { c.DrawRect(0, 0, W, H, bp); c.DrawRect(0, 0, W, H, blp); });
                EnsureImages();
                using var sp = new SKPaint();
                if (scanImg != null) sp.Shader = SKShader.CreateImage(scanImg, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
                k.Mask = Render(lw, lh, local, c => { c.DrawRect(0, 0, W, H, sp); c.DrawRect(0, 0, W, H, vp); });
                sp.Shader?.Dispose();
            }
            var tr = SKMatrix.CreateTranslation(lx, ly);
            k.Base0Shader = k.Base0.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, tr);
            k.Base1Shader = k.Base1.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, tr);

            // the ghost word, rotated -90 as it scrolls, fill + edge at full fade-in
            var heavy = SafeFace(() => Fonts.BarlowCondensed.Black);
            using (var gp = new SKPaint { Typeface = heavy, TextSize = 330, IsAntialias = true })
            using (var path = gp.GetTextPath(q.Label, 0, 0))
            using (var fill = new SKPaint { IsAntialias = true, Color = light.WithAlpha(26) })
            using (var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, Color = SKColors.White.WithAlpha(40) })
            {
                var gb = path.Bounds;
                k.GhostBounds = gb;
                if (gb.Width > 1)
                {
                    const float e = 3f;   // the edge stroke + antialiasing
                    // copy space: R(-90) T(-Left, 0) -> x' in [Top, Bottom], y' in [-Width, 0]
                    var copy = new SKRect(gb.Top - e, -gb.Width - e, gb.Bottom + e, e);
                    var sm = SKMatrix.CreateScale(m.ScaleX, m.ScaleY);
                    var cd = sm.MapRect(copy);
                    int gw = (int)Math.Ceiling(cd.Width) + 1, gh = (int)Math.Ceiling(cd.Height) + 1;
                    var gm = SKMatrix.CreateTranslation(-cd.Left, -cd.Top).PreConcat(sm)
                        .PreConcat(SKMatrix.CreateRotationDegrees(-90)).PreConcat(SKMatrix.CreateTranslation(-gb.Left, 0));
                    k.GhostImg = Render(gw, gh, gm, c => { c.DrawPath(path, fill); c.DrawPath(path, edge); });
                    k.GhostOx = cd.Left; k.GhostOy = cd.Top;
                }
            }

            // the panel: the same kit the live draw uses, with its own paints
            using var pk = new PanelKit();
            pk.Ensure(W, q.Label, light, q.ClassLogo != null, q.Wordmark != null, q.Series != null);
            var im = new PanelImages();
            k.Images = im;
            float ks = m.ScaleX;
            SKImage Piece(SKRect r, float scale, Action<SKCanvas> draw) =>
                Render((int)Math.Ceiling(r.Width * scale), (int)Math.Ceiling(r.Height * scale),
                       SKMatrix.CreateScale(scale, scale).PreConcat(SKMatrix.CreateTranslation(-r.Left, -r.Top)), draw);
            im.ShadowRect = pk.ShadowRect();
            im.Shadow = Piece(im.ShadowRect, ks, pk.DrawShadow);
            if (q.ClassLogo == null && pk.NameBlob != null)
            {
                // the blurred name glow and the name at full strength (the live draw scales / fades them during the punch-in)
                im.GlowRect = pk.NameGlowRect();
                im.Glow = Piece(im.GlowRect, ks, c => pk.DrawNameGlow(c, 1f));
                im.NameRect = pk.NameRect();
                im.Name = Piece(im.NameRect, ks * 2, c => pk.DrawName(c, 1f));
            }
            // settled panel layers at its hold position (scale 1, no shake), on the screen grid
            var pm = m.PreConcat(SKMatrix.CreateTranslation(W / 2f, H * PanelY));
            var layers = new Layer[PanelKit.LayerParts.Length];
            for (int i = 0; i < layers.Length; i++)
            {
                var (part, blend) = PanelKit.LayerParts[i];
                var r = pk.Bounds(part);
                if (r.IsEmpty) continue;
                var d = pm.MapRect(r);
                int x0 = (int)Math.Floor(d.Left), y0 = (int)Math.Floor(d.Top);
                int w = (int)Math.Ceiling(d.Right) - x0, h = (int)Math.Ceiling(d.Bottom) - y0;
                var lm = SKMatrix.CreateTranslation(-x0, -y0).PreConcat(pm);
                var img = Render(w, h, lm, c => pk.Draw(c, W, H, HoldStartMs, 0, q.Wordmark, q.Series, q.ClassLogo, part, null, null));
                layers[i] = new Layer(img, x0, y0, blend);
            }
            k.Panel = layers;
            OverlayLog.Write($"class transition '{q.Label}': still parts drawn in {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms (background)");
            return k;
        }
        catch
        {
            k.Dispose();
            throw;
        }
    }

    static SKImage Render(int w, int h, SKMatrix m, Action<SKCanvas> draw)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        using var surf = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul))
                         ?? throw new InvalidOperationException($"no {w}x{h} surface");
        var c = surf.Canvas;
        c.Clear(SKColors.Transparent);
        c.SetMatrix(m);
        draw(c);
        c.Flush();
        return surf.Snapshot();
    }

    static (SKShader Base, SKShader Bloom, SKShader Vignette) BackgroundShaders(float W, float H, SKColor classColor, SKColor light)
    {
        var deep = new SKColor(4, 6, 10);
        var tint = Mix(deep, classColor, 0.30f);
        return (SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(W * 0.35f, H),
                    new[] { Mix(deep, light, 0.20f), tint, deep, Mix(deep, light, 0.12f) }, new[] { 0f, 0.35f, 0.75f, 1f }, SKShaderTileMode.Clamp),
                SKShader.CreateRadialGradient(new SKPoint(W / 2f, H * PanelY), Math.Max(W, H) * 0.62f,
                    new[] { light.WithAlpha(80), light.WithAlpha(26), SKColors.Transparent }, new[] { 0f, 0.4f, 1f }, SKShaderTileMode.Clamp),
                SKShader.CreateRadialGradient(new SKPoint(W / 2f, H * PanelY), Math.Max(W, H) * 0.70f,
                    new[] { SKColors.Transparent, SKColors.Transparent, new SKColor(0, 0, 0, 165) }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp));
    }

    // ------------------------------------------------------------------ live resources

    void Ensure(float W, float H, SKColor classColor, string label, bool hasLogo, bool hasWordmark, bool hasSeries)
    {
        bool geo = W != builtW || H != builtH || classColor != builtColor;
        int flags = (hasLogo ? 1 : 0) | (hasWordmark ? 2 : 0) | (hasSeries ? 4 : 0);
        bool text = label != builtLabel || flags != builtFlags;
        if (!geo && !text) return;
        light = CarArt.LightColor(classColor);
        if (geo)
        {
            var (b, bl, v) = BackgroundShaders(W, H, classColor, light);
            Replace(basePaint, b); Replace(bloomPaint, bl); Replace(vignettePaint, v);
            // unit shaders (drawn through canvas transforms, so they never need rebuilding for motion)
            Replace(bandPaint, SKShader.CreateLinearGradient(new SKPoint(0, -1), new SKPoint(0, 1),
                new[] { SKColors.Transparent, light.WithAlpha(46), SKColors.White.WithAlpha(70), light.WithAlpha(46), SKColors.Transparent },
                new[] { 0f, 0.3f, 0.5f, 0.7f, 1f }, SKShaderTileMode.Clamp));
            Replace(streakPaint, SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1, 0),
                new[] { SKColors.Transparent, light.WithAlpha(150), light.WithAlpha(255) }, new[] { 0f, 0.7f, 1f }, SKShaderTileMode.Clamp));
            Replace(streakWhitePaint, SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1, 0),
                new[] { SKColors.Transparent, light.WithAlpha(120), SKColors.White }, new[] { 0f, 0.65f, 1f }, SKShaderTileMode.Clamp));
            Replace(chevronPaint, SKShader.CreateLinearGradient(new SKPoint(0, -0.42f), new SKPoint(0, 0.72f),
                new[] { SKColors.White, light, light.WithAlpha(60) }, new[] { 0f, 0.4f, 1f }, SKShaderTileMode.Clamp));
            Replace(burstPaint, SKShader.CreateRadialGradient(new SKPoint(0, 0), 1f,
                new[] { SKColors.White.WithAlpha(200), light.WithAlpha(90), SKColors.Transparent }, new[] { 0f, 0.35f, 1f }, SKShaderTileMode.Clamp));
            if (scanImg != null) Replace(scanPaint, SKShader.CreateImage(scanImg, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat));
            if (grainImg != null) Replace(grainPaint, SKShader.CreateImage(grainImg, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat));
            grainPaint.Color = SKColors.White.WithAlpha(80);
            builtW = W; builtH = H; builtColor = classColor;
        }

        kit.Ensure(W, label, light, hasLogo, hasWordmark, hasSeries);
        // the ghost word behind (live fallback until the cached picture is ready)
        ghost?.Dispose(); ghost = null;
        using (var gp = new SKPaint { Typeface = SafeFace(() => Fonts.BarlowCondensed.Black), TextSize = 330, IsAntialias = true })
        {
            ghost = gp.GetTextPath(label, 0, 0);
            ghostBounds = ghost.Bounds;
        }
        builtLabel = label; builtFlags = flags;
    }

    /// <summary>The glass panel: its paints, text and layout, and the drawing - used live and, with its own
    /// instance, by the background build of the settled panel layers.</summary>
    sealed class PanelKit : IDisposable
    {
        public const int Under = 1, Glint = 2, Over = 4, NameGlow = 8, Name = 16, LineGlow = 32, LineSeries = 64, All = 127;
        /// <summary>The settled panel's layers in draw order (the glint goes live between the first two).</summary>
        public static readonly (int Part, SKBlendMode Blend)[] LayerParts =
        {
            (Under, SKBlendMode.SrcOver), (Over, SKBlendMode.SrcOver), (NameGlow, SKBlendMode.Plus),
            (Name, SKBlendMode.SrcOver), (LineGlow, SKBlendMode.Plus), (LineSeries, SKBlendMode.SrcOver),
        };

        readonly SKPaint shadowPaint = new() { IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 16f) };
        readonly SKPaint darkGlassPaint = new() { IsAntialias = true };
        readonly SKPaint glassPaint = new() { IsAntialias = true };
        readonly SKPaint glassEdgePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        readonly SKPaint accentPaint = new() { IsAntialias = true };
        readonly SKPaint accentGlowPaint = new() { IsAntialias = true, BlendMode = SKBlendMode.Plus, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6f) };
        readonly SKPaint bracketPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f, StrokeCap = SKStrokeCap.Square };
        readonly SKPaint namePaint = new() { IsAntialias = true };
        readonly SKPaint nameGlowPaint = new() { IsAntialias = true, BlendMode = SKBlendMode.Plus, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10f) };
        readonly SKPaint kickerPaint = new() { IsAntialias = true };
        readonly SKPaint logoPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.High };
        readonly SKPaint glintPaint = new() { IsAntialias = false, BlendMode = SKBlendMode.Plus };
        readonly SKRoundRect panel = new();
        readonly SKFont nameFont = new() { Edging = SKFontEdging.Antialias, Subpixel = true };
        readonly SKFont kickerFont = new() { Edging = SKFontEdging.Antialias, Subpixel = true };
        public SKTextBlob? NameBlob, KickerBlob;
        float nameW, nameCap, kickerW, panelW, panelH, nameSize;
        bool hasLogo, hasWordmark, hasSeries;
        SKColor light;

        public PanelKit()
        {
            glintPaint.Shader = SKShader.CreateLinearGradient(new SKPoint(-1, 0), new SKPoint(1, 0),
                new[] { SKColors.Transparent, SKColors.White.WithAlpha(120), SKColors.Transparent }, null, SKShaderTileMode.Clamp);
        }

        public void Ensure(float W, string label, SKColor light, bool hasLogo, bool hasWordmark, bool hasSeries)
        {
            this.light = light; this.hasLogo = hasLogo; this.hasWordmark = hasWordmark; this.hasSeries = hasSeries;
            panelW = W - 56;
            var heavy = SafeFace(() => Fonts.BarlowCondensed.Black);
            var semi = SafeFace(() => Fonts.Inter.SemiBold);
            NameBlob?.Dispose(); NameBlob = null; KickerBlob?.Dispose(); KickerBlob = null;
            nameSize = 150f;
            nameFont.Typeface = heavy; kickerFont.Typeface = semi; kickerFont.Size = 15f;
            float maxW = panelW - 56;
            nameFont.Size = nameSize;
            float tracking = 0.04f;
            while (nameSize > 40 && Tracked(nameFont, label, tracking) > maxW) { nameSize -= 4; nameFont.Size = nameSize; }
            NameBlob = MakeBlob(nameFont, label, tracking, out nameW);
            nameCap = nameSize * 0.70f;
            KickerBlob = MakeBlob(kickerFont, label == "OVERALL" ? "LIVE  ·  ALL CLASSES" : "LIVE  ·  CLASS STANDINGS", 0.22f, out kickerW);
            float block = hasLogo ? 96f : nameCap;
            // must match Draw's stack: pad, [wordmark 26+18], kicker 31, class block, 20, line 4, [18 + series 52], pad
            panelH = PadTop + (hasWordmark ? WordmarkH + 18 : 0) + 31 + block + 20 + 4 + (hasSeries ? 18 + SeriesH : 0) + PadBottom;
            namePaint.Shader?.Dispose();
            namePaint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, -nameCap * 0.6f), new SKPoint(0, nameCap * 0.6f),
                new[] { SKColors.White, Mix(SKColors.White, light, 0.18f) }, null, SKShaderTileMode.Clamp);
            Replace(glassPaint, SKShader.CreateLinearGradient(new SKPoint(0, -panelH / 2f), new SKPoint(0, panelH / 2f),
                new[] { SKColors.White.WithAlpha(46), SKColors.White.WithAlpha(12), light.WithAlpha(30) }, new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp));
        }

        // panel-local y of each block (the same stack Draw walks)
        float YKicker => -panelH / 2f + PadTop + (hasWordmark ? WordmarkH + 18 : 0);
        float YClass => YKicker + (KickerBlob != null ? 31 : 0);
        float YLine => YClass + (hasLogo ? 96f : NameBlob != null ? nameCap : 0) + 20;

        /// <summary>The name glow's picture area in the name's own coordinates (origin = the name block centre,
        /// as Draw translates it before scaling): the blob plus three blur radii.</summary>
        public SKRect NameGlowRect()
        {
            var b = NameBlob!.Bounds;
            b.Offset(-nameW / 2, nameCap / 2);
            b.Inflate(34, 34);
            return b;
        }

        /// <summary>The name's own picture area (text bounds + antialiasing), in the name's own coordinates.</summary>
        public SKRect NameRect()
        {
            var b = NameBlob!.Bounds;
            b.Offset(-nameW / 2, nameCap / 2);
            b.Inflate(3, 3);
            return b;
        }

        public void DrawName(SKCanvas c, float na)
        {
            // white-hot on impact, settling to white
            namePaint.Color = SKColors.White.WithAlpha(A(255 * na));
            c.DrawText(NameBlob!, -nameW / 2, nameCap / 2, namePaint);
        }

        /// <summary>The panel's drop shadow area (panel centre coordinates): the glass 12 px down plus three blur radii.</summary>
        public SKRect ShadowRect()
        {
            var r = new SKRect(-panelW / 2f, -panelH / 2f + 12, panelW / 2f, panelH / 2f + 12);
            r.Inflate(50, 50);
            return r;
        }

        public void DrawShadow(SKCanvas c)
        {
            panel.SetRect(new SKRect(-panelW / 2f, -panelH / 2f, panelW / 2f, panelH / 2f), 10, 10);
            shadowPaint.Color = new SKColor(0, 0, 0, 150);
            c.Save(); c.Translate(0, 12); c.DrawRoundRect(panel, shadowPaint); c.Restore();
        }

        /// <summary>The name glow at full strength in the name's own coordinates.</summary>
        public void DrawNameGlow(SKCanvas c, float na)
        {
            nameGlowPaint.Color = light.WithAlpha(A(170 * na));
            c.DrawText(NameBlob!, -nameW / 2, nameCap / 2, nameGlowPaint);
        }

        /// <summary>Settled-panel bounds of one layer (panel-local, scale 1), generous for blurs and brackets.</summary>
        public SKRect Bounds(int part)
        {
            var r = new SKRect(-panelW / 2f, -panelH / 2f, panelW / 2f, panelH / 2f);
            switch (part)
            {
                case Under: r.Inflate(52, 52); r.Bottom += 12; return r;
                case Over: r.Inflate(36, 36); return r;
                case NameGlow:
                    if (hasLogo || NameBlob == null) return SKRect.Empty;
                    var g = NameGlowRect(); g.Offset(0, YClass + nameCap / 2); return g;
                case Name:
                    if (hasLogo) return new SKRect(r.Left - 4, YClass - 4, r.Right + 4, YClass + 100);
                    if (NameBlob == null) return SKRect.Empty;
                    var n = NameBlob.Bounds; n.Offset(-nameW / 2, YClass + nameCap); n.Inflate(4, 4); return n;
                case LineGlow: return new SKRect(r.Left - 24, YLine - 24, r.Right + 24, YLine + 28);
                case LineSeries: return new SKRect(r.Left - 4, YLine - 4, r.Right + 4, r.Bottom + 4);
                default: return SKRect.Empty;
            }
        }

        /// <summary>Draws the panel's <paramref name="parts"/> with the canvas at the panel centre's lane position
        /// (see the stinger's step 7). Live: <see cref="All"/>; the settled layers draw one part each.</summary>
        public void Draw(SKCanvas c, float W, float H, double t, double exitK,
                         SKBitmap? wordmark, SKBitmap? series, SKBitmap? classLogo, int parts,
                         PanelImages? im, SKPaint? imPaint)
        {
            double pk = (t - PunchMs) / 360.0;
            if (pk <= 0) return;
            float alpha = (float)Smooth((t - PunchMs) / 110.0);
            float scale = (float)(1.38 - 0.38 * OutBack(pk) + 0.05 * exitK);
            float shake = t < PunchMs + 600 ? (float)(Math.Sin((t - PunchMs) * 0.09) * 7 * Math.Exp(-(t - PunchMs) / 150.0)) : 0f;
            bool centred = parts != All;   // a settled layer / the glint: the caller already stands at the panel centre
            float cx = centred ? 0 : W / 2f, cy = centred ? 0 : H * PanelY + shake;

            c.Save();
            c.Translate(cx, cy);
            c.Scale(scale, scale);
            var r = new SKRect(-panelW / 2f, -panelH / 2f, panelW / 2f, panelH / 2f);
            panel.SetRect(r, 10, 10);

            if ((parts & Under) != 0)
            {
                if (im?.Shadow != null && imPaint != null)
                {
                    // the blurred shadow as a picture, zoomed with the panel (a blur's look survives scaling)
                    imPaint.Color = new SKColor(255, 255, 255, (byte)(255 * alpha));
                    c.DrawImage(im.Shadow, im.ShadowRect, imPaint);
                }
                else
                {
                    shadowPaint.Color = new SKColor(0, 0, 0, (byte)(150 * alpha));
                    c.Save(); c.Translate(0, 12); c.DrawRoundRect(panel, shadowPaint); c.Restore();
                }
                darkGlassPaint.Color = new SKColor(6, 9, 14, (byte)(205 * alpha));
                c.DrawRoundRect(panel, darkGlassPaint);
                glassPaint.Color = SKColors.White.WithAlpha(A(255 * alpha * (0.85 + 0.15 * exitK)));
                c.DrawRoundRect(panel, glassPaint);
            }

            // glint across the glass + the class accent bar on top, clipped to the panel
            if ((parts & (Glint | Over)) != 0)
            {
                c.Save();
                c.ClipRoundRect(panel, SKClipOperation.Intersect, true);
                if ((parts & Glint) != 0 && t > GlintStartMs)
                {
                    double gk = ((t - GlintStartMs) % GlintEveryMs) / 650.0;
                    if (gk < 1)
                    {
                        float gx = (float)(-panelW + gk * panelW * 2.2);
                        glintPaint.Color = SKColors.White.WithAlpha(A(90 * alpha));
                        c.Save(); c.Translate(gx, 0); c.Skew(-0.55f, 0); c.Scale(46, panelH); c.DrawRect(-1, -0.5f, 2, 1, glintPaint); c.Restore();
                    }
                }
                if ((parts & Over) != 0)
                {
                    accentPaint.Color = light.WithAlpha(A(255 * alpha));
                    c.DrawRect(r.Left, r.Top, r.Width, 5, accentPaint);
                }
                c.Restore();
            }
            float y = r.Top + PadTop;
            float maxW = panelW - 48;
            if ((parts & Over) != 0)
            {
                glassEdgePaint.Color = SKColors.White.WithAlpha(A(120 * alpha));
                c.DrawRoundRect(panel, glassEdgePaint);

                // corner brackets just outside the glass
                bracketPaint.Color = light.WithAlpha(A(230 * alpha));
                float bo = 9f + (float)(12 * (1 - OutCubic(pk))), bl = 22f;
                DrawBracket(c, r.Left - bo, r.Top - bo, bl, 1, 1);
                DrawBracket(c, r.Right + bo, r.Top - bo, bl, -1, 1);
                DrawBracket(c, r.Left - bo, r.Bottom + bo, bl, 1, -1);
                DrawBracket(c, r.Right + bo, r.Bottom + bo, bl, -1, -1);
            }

            // content stack
            if (wordmark != null && wordmark.Height > 0)
            {
                if ((parts & Over) != 0)
                {
                    float wa = alpha * (float)Smooth((t - LogosMs) / 280.0);
                    float h = WordmarkH, w = Math.Min(maxW * 0.7f, wordmark.Width * h / wordmark.Height);
                    float hh = w * wordmark.Height / wordmark.Width;
                    logoPaint.Color = SKColors.White.WithAlpha(A(255 * wa));
                    c.DrawBitmap(wordmark, new SKRect(-w / 2, y + (h - hh) / 2, w / 2, y + (h + hh) / 2), logoPaint);
                }
                y += WordmarkH + 18;
            }
            else if (hasWordmark) y += WordmarkH + 18;   // a settled-glint call passes no bitmaps: keep the stack
            // kicker
            if (KickerBlob != null)
            {
                if ((parts & Over) != 0)
                {
                    float ka = alpha * (float)Smooth((t - KickerMs) / 260.0);
                    float slide = (float)(18 * (1 - OutCubic((t - KickerMs) / 380.0)));
                    kickerPaint.Color = Mix(SKColors.White, light, 0.45f).WithAlpha(A(235 * ka));
                    c.DrawText(KickerBlob, -kickerW / 2 + slide, y + 15, kickerPaint);
                }
                y += 15 + 16;
            }
            // the class: logo, else the name in big type
            double nk = (t - NameMs) / 280.0;
            float na = alpha * (float)Smooth(nk * 1.6);
            float ns = (float)(1.45 - 0.45 * OutCubic(nk));
            float blockH;
            if (classLogo != null && classLogo.Width > 0)
            {
                float lw = Math.Min(maxW, classLogo.Width * 96f / classLogo.Height), lh = lw * classLogo.Height / classLogo.Width;
                blockH = 96f;
                if ((parts & Name) != 0)
                {
                    c.Save(); c.Translate(0, y + 48f); c.Scale(ns, ns);
                    logoPaint.Color = SKColors.White.WithAlpha(A(255 * na));
                    c.DrawBitmap(classLogo, new SKRect(-lw / 2, -lh / 2, lw / 2, lh / 2), logoPaint);
                    c.Restore();
                }
            }
            else if (hasLogo) blockH = 96f;
            else if (NameBlob != null)
            {
                blockH = nameCap;
                if ((parts & (NameGlow | Name)) != 0 && A(255 * na) > 0)   // nothing to draw before the name fades in
                {
                    c.Save(); c.Translate(0, y + nameCap / 2); c.Scale(ns, ns);
                    if ((parts & NameGlow) != 0)
                    {
                        if (im?.Glow != null && imPaint != null)
                        {
                            // the cached blurred glow, scaled and faded with the name
                            imPaint.Color = SKColors.White.WithAlpha(A(255 * na));
                            imPaint.BlendMode = SKBlendMode.Plus;
                            c.DrawImage(im.Glow, im.GlowRect, imPaint);
                            imPaint.BlendMode = SKBlendMode.SrcOver;
                        }
                        else DrawNameGlow(c, na);
                    }
                    if ((parts & Name) != 0)
                    {
                        if (im?.Name != null && imPaint != null)
                        {
                            // the big type as a picture while it zooms (text this large is re-outlined every frame)
                            // (bilinear from a 2x picture: the zoom never shrinks it past half, so no mip levels to build)
                            imPaint.Color = SKColors.White.WithAlpha(A(255 * na));
                            c.DrawImage(im.Name, im.NameRect, imPaint);
                        }
                        else DrawName(c, na);
                    }
                    c.Restore();
                }
            }
            else blockH = 0;
            y += blockH + 20;
            // accent line growing from the middle
            float lk = (float)OutCubic((t - LineMs) / 420.0);
            if (lk > 0 && (parts & (LineGlow | LineSeries)) != 0)
            {
                float lw2 = (maxW - 20) * lk / 2f;
                if ((parts & LineGlow) != 0)
                {
                    accentGlowPaint.Color = light.WithAlpha(A(200 * alpha));
                    c.DrawRect(-lw2, y, lw2 * 2, 4, accentGlowPaint);
                }
                if ((parts & LineSeries) != 0)
                {
                    accentPaint.Color = light.WithAlpha(A(255 * alpha));
                    c.DrawRect(-lw2, y, lw2 * 2, 4, accentPaint);
                }
            }
            y += 4 + 18;
            if (series != null && series.Height > 0 && (parts & LineSeries) != 0)
            {
                float sa = alpha * (float)Smooth((t - LogosMs - 120) / 300.0);
                float h = SeriesH, w = Math.Min(maxW * 0.8f, series.Width * h / series.Height);
                float hh = w * series.Height / series.Width;
                logoPaint.Color = SKColors.White.WithAlpha(A(255 * sa));
                c.DrawBitmap(series, new SKRect(-w / 2, y + (h - hh) / 2, w / 2, y + (h + hh) / 2), logoPaint);
            }
            c.Restore();
        }

        void DrawBracket(SKCanvas c, float x, float y, float len, int dx, int dy)
        {
            c.DrawLine(x, y, x + dx * len, y, bracketPaint);
            c.DrawLine(x, y, x, y + dy * len, bracketPaint);
        }

        public void Dispose()
        {
            foreach (var p in new[] { shadowPaint, darkGlassPaint, glassPaint, glassEdgePaint, accentPaint, accentGlowPaint, bracketPaint,
                                      namePaint, nameGlowPaint, kickerPaint, logoPaint, glintPaint })
            { p.Shader?.Dispose(); p.Dispose(); }
            panel.Dispose(); nameFont.Dispose(); kickerFont.Dispose();
            NameBlob?.Dispose(); KickerBlob?.Dispose();
        }
    }

    static SKTypeface SafeFace(Func<SKTypeface> f) { try { return f(); } catch { return SKTypeface.Default; } }

    static float Tracked(SKFont f, string s, float trackEm)
    {
        var w = Widths(f, s);
        float sum = 0; foreach (var x in w) sum += x;
        return sum + Math.Max(0, w.Length - 1) * trackEm * f.Size;
    }

    static float[] Widths(SKFont f, string s)
    {
        var glyphs = new ushort[f.CountGlyphs(s)];
        f.GetGlyphs(s, glyphs);
        var w = new float[glyphs.Length];
        f.GetGlyphWidths(glyphs, w, Span<SKRect>.Empty, null);
        return w;
    }

    static SKTextBlob? MakeBlob(SKFont f, string s, float trackEm, out float width)
    {
        width = 0;
        if (string.IsNullOrEmpty(s)) return null;
        var w = Widths(f, s);
        var xs = new float[w.Length];
        float x = 0;
        for (int i = 0; i < w.Length; i++) { xs[i] = x; x += w[i] + trackEm * f.Size; }
        width = x - trackEm * f.Size;
        return SKTextBlob.CreateHorizontal(s, f, xs, 0);
    }

    static void Replace(SKPaint p, SKShader s) { var old = p.Shader; p.Shader = s; old?.Dispose(); }

    static SKColor Mix(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t), (byte)(a.Blue + (b.Blue - a.Blue) * t), 255);

    static void EnsureImages()
    {
        lock (imgGate)
        {
            if (grainImg == null)
            {
                var rnd = new Random(77);
                var info = new SKImageInfo(128, 128, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var bmp = new SKBitmap(info);
                for (int y = 0; y < 128; y++)
                    for (int x = 0; x < 128; x++)
                    {
                        // grey noise around mid-grey: Overlay leaves mid-grey unchanged, so it only adds texture
                        byte g = (byte)Math.Clamp(128 + (rnd.NextDouble() + rnd.NextDouble() - 1) * 70, 0, 255);
                        bmp.SetPixel(x, y, new SKColor(g, g, g, 255));
                    }
                bmp.SetImmutable();
                grainImg = SKImage.FromBitmap(bmp);
            }
            if (scanImg == null)
            {
                var info = new SKImageInfo(1, 3, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var bmp = new SKBitmap(info);
                bmp.SetPixel(0, 0, new SKColor(0, 0, 0, 0));
                bmp.SetPixel(0, 1, new SKColor(0, 0, 0, 0));
                bmp.SetPixel(0, 2, new SKColor(0, 0, 0, 34));
                bmp.SetImmutable();
                scanImg = SKImage.FromBitmap(bmp);
            }
        }
    }

    public void Dispose()
    {
        foreach (var p in new[] { basePaint, bloomPaint, vignettePaint, bandPaint, streakPaint, streakWhitePaint, chevronPaint, chevronEdgePaint,
                                  flashPaint, burstPaint, ghostFillPaint, ghostEdgePaint, railPaint, tickPaint, edgeBarPaint, grainPaint, scanPaint,
                                  imgPaint, imgSmoothPaint, lerpPaint })
        { p.Shader?.Dispose(); p.Dispose(); }
        armL.Dispose(); armR.Dispose(); foreach (var s in edgeStrip) s.Dispose();
        kit.Dispose();
        ghost?.Dispose();
        // a background build still running finishes on its own; its pictures are dropped
        if (building is { IsCompleted: true } b && b.Status == TaskStatus.RanToCompletion) b.Result?.Dispose();
        else building?.ContinueWith(x => { if (x.Status == TaskStatus.RanToCompletion) x.Result?.Dispose(); });
        cache?.Dispose(); cache = null;
    }
}
