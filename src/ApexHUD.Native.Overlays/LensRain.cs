using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// 2026-10-26 (#234, Angelo: rain on the lens like real TV broadcasts in the rain). When it is actually raining
/// (iRacing Precipitation) and the camera on air is a trackside / TV camera, water drops sit on the "lens": mostly
/// small, a few big, semi-transparent with a darker rim, a bright highlight and a soft lighter fill. Some slowly grow,
/// merge and run down leaving a faint streak; new ones land at a rate tied to how hard it rains. A camera cut is a
/// fresh lens (most drops cleared). Never on onboard / cockpit / chase cameras (the car has its own windscreen) or on
/// the blimp / helicopter.
///
/// <para>Cheap at 4K / 60 fps: three drop sprites are drawn ONCE into images and blitted; at most
/// <see cref="MaxDrops"/> drops and <see cref="MaxTrails"/> streaks; no per-pixel work and no per-frame allocation.
/// Works in the compositor's 1080p design space (the host scales the whole canvas up for 4K).</para>
/// </summary>
public sealed class LensRain : IDisposable
{
    /// <summary>Most drops on the lens at once.</summary>
    public const int MaxDrops = 150;
    const int MaxTrails = 40;
    const int SpriteSize = 128;
    const float RunRadius = 9.5f;        // design px: a drop this big starts to run down

    struct Drop
    {
        public float X, Y, R, Vy, Wobble, Grow, Age, FadeIn;
        public byte Kind;                // 0 crisp, 1 soft (out of focus)
        public bool Alive, Running;
        public float TrailTop;
    }

    struct Trail
    {
        public float X, Y0, Y1, W, Age;
        public bool Alive;
    }

    readonly Drop[] drops = new Drop[MaxDrops];
    readonly Trail[] trails = new Trail[MaxTrails];
    readonly Random rng = new(2341);
    readonly SKPaint blit = new() { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
    SKImage? crisp, soft, streak;

    double lastMs = -1;
    float master;                        // 0..1 overall fade (rain on/off, TV camera or not)
    float intensity;                     // smoothed precipitation 0..1
    bool raining;
    float spawnDebt;
    int lastGroup = int.MinValue, lastCar = int.MinValue;
    string lastCam = "";
    int width = 1920, height = 1080;

    /// <summary>True while anything is on the lens or fading (the compositor keeps presenting).</summary>
    public bool Active => master > 0.002f || raining && WantsLens;

    /// <summary>The camera on air at the last tick is a trackside / TV camera.</summary>
    public bool WantsLens { get; private set; }

    /// <summary>Onboard / in-car words: the car's own windscreen, so no lens drops (iRacing group or camera names).</summary>
    static readonly string[] OnboardWords =
        { "cockpit", "roll bar", "rollbar", "gyro", "nose", "gearbox", "susp", "wing", "onboard", "chase", "rear", "in car", "incar" };
    static readonly string[] AerialWords = { "blimp", "chopper", "heli", "aerial", "drone" };

    /// <summary>
    /// A trackside / TV camera (TV1, TV2, TV3, TV Static, TV Mixed, Scenic, Pit Lane...) - not an onboard, cockpit or
    /// chase view and not the blimp / helicopter. Both the group's name and the live camera's name are checked, because a
    /// TV group cuts between trackside and in-car cameras.
    /// </summary>
    public static bool IsTvCamera(string groupName, string cameraName)
    {
        string g = (groupName ?? "").ToLowerInvariant(), c = (cameraName ?? "").ToLowerInvariant();
        if (g.Length == 0 && c.Length == 0) return false;
        foreach (var w in AerialWords)
            if (g.Contains(w, StringComparison.Ordinal) || c.Contains(w, StringComparison.Ordinal)) return false;
        foreach (var w in OnboardWords)
            if (g.Contains(w, StringComparison.Ordinal) || c.Contains(w, StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>Advance the drops one frame. Call every present while the graphic is allowed.</summary>
    public void Tick(RaceSnapshot snap, double nowMs, int w, int h)
    {
        width = Math.Max(1, w); height = Math.Max(1, h);
        float dt = lastMs < 0 ? 0.016f : (float)Math.Clamp((nowMs - lastMs) / 1000.0, 0, 0.1);
        lastMs = nowMs;

        float precip = snap.IsConnected ? Math.Clamp(snap.Extras.PrecipNow, 0f, 1f) : 0f;
        // hysteresis so a drizzle on the edge doesn't flicker the effect
        if (!raining && precip > 0.05f) raining = true;
        else if (raining && precip < 0.03f) raining = false;
        intensity += ((raining ? precip : 0f) - intensity) * Math.Min(1f, dt / 2.5f);

        var ob = snap.Onboard;
        WantsLens = snap.IsConnected && IsTvCamera(ob.GroupOnlyName, ob.CamGroupName);

        // camera cut (another group, another camera or another car) = a fresh lens: keep only a few drops
        string cam = ob.CamGroupName ?? "";
        if (ob.CamGroup != lastGroup || ob.CamCarIdx != lastCar || !string.Equals(cam, lastCam, StringComparison.Ordinal))
        {
            bool first = lastGroup == int.MinValue;
            lastGroup = ob.CamGroup; lastCar = ob.CamCarIdx; lastCam = cam;
            if (!first) FreshLens();
        }

        // fade in / out (~0.6 s) with the rain and the camera type
        float target = raining && WantsLens ? 1f : 0f;
        master += (target - master) * Math.Min(1f, dt / 0.6f);
        if (Math.Abs(target - master) < 0.002f) master = target;
        if (master <= 0f && target <= 0f) { Clear(); return; }

        // new drops land at a rate tied to the rain (heavy rain ~45 a second, light drizzle a few)
        if (raining && WantsLens)
        {
            spawnDebt += dt * (3f + 42f * intensity);
            while (spawnDebt >= 1f) { spawnDebt -= 1f; Spawn(); }
        }
        else spawnDebt = 0;

        Step(dt);
    }

    void FreshLens()
    {
        for (int i = 0; i < drops.Length; i++)
            if (drops[i].Alive && (drops[i].Running || rng.NextDouble() > 0.15)) drops[i].Alive = false;
        for (int i = 0; i < trails.Length; i++) trails[i].Alive = false;
    }

    void Clear()
    {
        for (int i = 0; i < drops.Length; i++) drops[i].Alive = false;
        for (int i = 0; i < trails.Length; i++) trails[i].Alive = false;
    }

    void Spawn()
    {
        // size: mostly small, a few big
        double u = rng.NextDouble();
        float r = u < 0.93 ? 2.2f + 4.2f * (float)Math.Pow(rng.NextDouble(), 2.2) : 7f + 7f * (float)rng.NextDouble();
        float x = (float)rng.NextDouble() * width, y = (float)rng.NextDouble() * height * 0.96f;

        // landing on a drop merges into it
        for (int i = 0; i < drops.Length; i++)
        {
            ref var d = ref drops[i];
            if (!d.Alive) continue;
            float dx = d.X - x, dy = d.Y - y, rr = (d.R + r) * 0.8f;
            if (dx * dx + dy * dy < rr * rr) { d.R = MathF.Min(18f, MathF.Sqrt(d.R * d.R + r * r)); return; }
        }

        int slot = -1;
        for (int i = 0; i < drops.Length; i++) if (!drops[i].Alive) { slot = i; break; }
        if (slot < 0)
        {
            // full: the oldest small resting drop makes room
            float oldest = -1;
            for (int i = 0; i < drops.Length; i++)
                if (!drops[i].Running && drops[i].Age > oldest) { oldest = drops[i].Age; slot = i; }
            if (slot < 0) return;
        }
        drops[slot] = new Drop
        {
            X = x, Y = y, R = r, Alive = true,
            Kind = (byte)(rng.NextDouble() < 0.3 ? 1 : 0),
            Grow = rng.NextDouble() < 0.25 ? 0.25f + 0.6f * (float)rng.NextDouble() : 0f,   // some drops slowly swell
            Wobble = (float)(rng.NextDouble() * Math.PI * 2),
        };
    }

    void Step(float dt)
    {
        for (int i = 0; i < drops.Length; i++)
        {
            ref var d = ref drops[i];
            if (!d.Alive) continue;
            d.Age += dt;
            d.FadeIn = MathF.Min(1f, d.FadeIn + dt / 0.12f);
            if (raining) d.R = MathF.Min(18f, d.R + d.Grow * intensity * dt);
            if (!d.Running && d.R >= RunRadius && d.Kind == 0) { d.Running = true; d.TrailTop = d.Y; d.Vy = 8f; }
            if (!d.Running) continue;

            // runs down, speeding up, a little wobble, leaving water behind it as it goes
            d.Vy = MathF.Min(150f, d.Vy + 110f * dt);
            d.Wobble += dt * 3.1f;
            d.X += MathF.Sin(d.Wobble) * 9f * dt;
            d.Y += d.Vy * dt;
            d.R -= 0.35f * dt;
            // swallow the small drops in its path
            for (int j = 0; j < drops.Length; j++)
            {
                if (j == i) continue;
                ref var o = ref drops[j];
                if (!o.Alive || o.Running) continue;
                float dx = o.X - d.X, dy = o.Y - d.Y, rr = d.R + o.R * 0.6f;
                if (dx * dx + dy * dy < rr * rr) { d.R = MathF.Min(18f, MathF.Sqrt(d.R * d.R + o.R * o.R * 0.6f)); o.Alive = false; }
            }
            if (d.Y - d.TrailTop > 60f || d.Y - d.R > height || d.R < 5f) LayTrail(ref d);
            if (d.Y - d.R > height) d.Alive = false;
            else if (d.R < 5f) { d.Running = false; d.Grow = 0f; }   // too small to keep going: it stops
        }
        for (int i = 0; i < trails.Length; i++)
        {
            ref var t = ref trails[i];
            if (!t.Alive) continue;
            t.Age += dt;
            if (t.Age > 4f) t.Alive = false;
        }
    }

    void LayTrail(ref Drop d)
    {
        int slot = -1; float oldest = -1;
        for (int i = 0; i < trails.Length; i++)
        {
            if (!trails[i].Alive) { slot = i; break; }
            if (trails[i].Age > oldest) { oldest = trails[i].Age; slot = i; }
        }
        if (slot >= 0 && d.Y - d.R * 0.6f - d.TrailTop > 4f) trails[slot] = new Trail { X = d.X, Y0 = d.TrailTop, Y1 = d.Y - d.R * 0.6f, W = MathF.Max(2f, d.R * 0.7f), Alive = true };
        d.TrailTop = d.Y - d.R * 0.6f;
    }

    /// <summary>Draw the lens (full screen, design space). Draws nothing when the lens is clear.</summary>
    public void Draw(SKCanvas c)
    {
        if (master <= 0.002f) return;
        EnsureSprites();
        // streaks first (faint), then the drops over them
        for (int i = 0; i < trails.Length; i++)
        {
            ref var t = ref trails[i];
            if (!t.Alive) continue;
            float a = master * 0.4f * (1f - t.Age / 4f);
            if (a <= 0.01f) continue;
            blit.Color = SKColors.White.WithAlpha((byte)(255 * a));
            c.DrawImage(streak!, new SKRect(t.X - t.W / 2f, t.Y0, t.X + t.W / 2f, t.Y1), blit);
        }
        for (int i = 0; i < drops.Length; i++)
        {
            ref var d = ref drops[i];
            if (!d.Alive) continue;
            float a = master * d.FadeIn * (d.Kind == 1 ? 0.75f : 1f);
            if (a <= 0.01f) continue;
            blit.Color = SKColors.White.WithAlpha((byte)(255 * a));
            // running drops stretch a little (taller, narrower); a sprite covers 1.25x the drop for its soft rim
            float rx = d.R * 1.25f * (d.Running ? 0.9f : 1f), ry = d.R * 1.25f * (d.Running ? 1.18f : 1f);
            c.DrawImage(d.Kind == 1 ? soft! : crisp!, new SKRect(d.X - rx, d.Y - ry, d.X + rx, d.Y + ry), blit);
        }
    }

    // ---- the sprites, drawn once ----

    void EnsureSprites()
    {
        crisp ??= MakeDrop(false);
        soft ??= MakeDrop(true);
        streak ??= MakeStreak();
    }

    static SKImage MakeDrop(bool outOfFocus)
    {
        var info = new SKImageInfo(SpriteSize, SpriteSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        float cx = SpriteSize / 2f, cy = SpriteSize / 2f, r = SpriteSize / 2f / 1.25f;
        float blur = outOfFocus ? r * 0.18f : r * 0.05f;
        using var mask = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur);

        // soft lighter fill - a water drop refracts the brighter sky into its lower half, darker on top
        using (var fill = new SKPaint { IsAntialias = true, MaskFilter = mask })
        {
            fill.Shader = SKShader.CreateLinearGradient(new SKPoint(cx, cy - r), new SKPoint(cx, cy + r),
                new[] { new SKColor(10, 14, 20, outOfFocus ? (byte)40 : (byte)60), new SKColor(255, 255, 255, outOfFocus ? (byte)30 : (byte)38),
                        new SKColor(255, 255, 255, outOfFocus ? (byte)55 : (byte)80) },
                new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp);
            c.DrawCircle(cx, cy, r, fill);
        }
        // darker rim so it reads over a bright picture
        using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = r * 0.16f, MaskFilter = mask })
        {
            rim.Color = new SKColor(0, 0, 0, outOfFocus ? (byte)55 : (byte)95);
            c.DrawCircle(cx, cy, r * 0.93f, rim);
        }
        // a thin bright inner edge along the bottom (light gathering in the drop)
        using (var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = r * 0.07f, MaskFilter = mask })
        {
            edge.Color = new SKColor(255, 255, 255, outOfFocus ? (byte)45 : (byte)90);
            c.DrawArc(new SKRect(cx - r * 0.78f, cy - r * 0.78f, cx + r * 0.78f, cy + r * 0.78f), 25f, 130f, false, edge);
        }
        // bright specular highlight, upper left, so it reads as water over any picture
        using (var spec = new SKPaint { IsAntialias = true })
        {
            using var specBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, r * (outOfFocus ? 0.12f : 0.05f));
            spec.MaskFilter = specBlur;
            spec.Color = new SKColor(255, 255, 255, outOfFocus ? (byte)150 : (byte)235);
            c.DrawOval(new SKRect(cx - r * 0.55f, cy - r * 0.62f, cx - r * 0.12f, cy - r * 0.30f), spec);
            spec.Color = new SKColor(255, 255, 255, outOfFocus ? (byte)50 : (byte)90);
            c.DrawCircle(cx + r * 0.38f, cy + r * 0.40f, r * 0.08f, spec);
        }
        return surface.Snapshot();
    }

    static SKImage MakeStreak()
    {
        const int sw = 32, sh = 128;
        var info = new SKImageInfo(sw, sh, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        using var mask = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2.5f);
        using var p = new SKPaint { IsAntialias = true, MaskFilter = mask };
        // fainter at the top (older water), a wet edge either side
        p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, sh),
            new[] { new SKColor(255, 255, 255, 10), new SKColor(255, 255, 255, 60) }, null, SKShaderTileMode.Clamp);
        c.DrawRoundRect(new SKRect(sw * 0.3f, 4, sw * 0.7f, sh - 4), sw * 0.2f, sw * 0.2f, p);
        p.Shader = null;
        p.Style = SKPaintStyle.Stroke; p.StrokeWidth = 1.6f; p.Color = new SKColor(0, 0, 0, 45);
        c.DrawRoundRect(new SKRect(sw * 0.3f, 4, sw * 0.7f, sh - 4), sw * 0.2f, sw * 0.2f, p);
        return surface.Snapshot();
    }

    public void Dispose()
    {
        crisp?.Dispose(); soft?.Dispose(); streak?.Dispose();
        blit.Dispose();
    }
}
