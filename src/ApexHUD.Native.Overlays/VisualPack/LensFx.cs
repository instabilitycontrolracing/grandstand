using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// W11 (2026-10-27, round 2): broadcast lens effects lined up with the sim's real sun. Angelo approved it "as long as timing
/// is perfect and it lines up perfectly", so the flare is only drawn where its position can be worked out (see
/// <see cref="SunAlign"/>): the sim's SolarAzimuth / SolarAltitude, a car-mounted camera looking along the car (chase,
/// nose, gyro - or backwards: rear chase, gearbox) and that car's true heading. Then the sun is projected through the
/// camera and the flare sits on it, following it as the car turns. Anything unknown (trackside TV cameras, the cockpit,
/// no sim sun, the outline not calibrated, a corner where the outline's direction is less sure) = no flare and no glow.
/// <para>Timing: a camera cut drops everything at once (the new shot starts clean), waits 250 ms for the camera to
/// settle, then eases in over 350 ms; the sun leaving the frame fades it out in 250 ms. Strength follows the sky
/// (overcast = none) and how low the sun is (golden hour = warmest).</para>
/// <para>Base: soft glow + starburst + short streak on the sun, warm wash from the sun's side. Elevated ("cinema
/// glass"): long anamorphic streak, hexagonal ghosts on the line through the frame centre (where a real lens puts them),
/// dust motes in the light (round 3: no heat haze). Sprites drawn once; a frame is a dozen image draws, no
/// allocation.</para>
/// <para>Round 3 (2026-10-27, Angelo: "I want to see this in a way that will not get caught out being in an area that is
/// in the way"): the effect only lives in FREE sky. It is drawn only while the sun sits in the top band (y &lt; 620) of the
/// left or right third - never the centre third where the cars are - and well clear (260 px) of every graphic
/// <see cref="VpLayout"/> knows (tower, session bar and watch, ticker, radio card, battle card and map, story / custom /
/// featured cards, the pack's own cards; the onboard treatment turns it off). It fades out (160 ms) as soon as anything
/// comes up near it and back in (350 ms) when the space clears. Everything it draws is confined to the sun's own third
/// and band and has soft holes cut round every graphic (and the centre third), so a streak or the wash can't reach them.
/// The ghost chain is short (three small ghosts close to the sun), the streaks are shorter, and the heat haze (low in the
/// frame, over the cars) is gone.</para>
/// </summary>
public sealed class LensFx : VpGraphic
{
    SKImage? glow, star, ghost, streak, mote, haze;
    float vis, wash, hot, warmth;
    double lastMs = -1, cutAt = -1e12;
    int cameraKey = int.MinValue;
    SKPoint src = new(1500, 120);
    readonly Random rng = new(5);
    readonly SKPaint add = new() { IsAntialias = true, BlendMode = SKBlendMode.Plus, FilterQuality = SKFilterQuality.Medium };
    readonly SKPaint screen = new() { IsAntialias = true, BlendMode = SKBlendMode.Screen };
    readonly SKPaint warmWash = new() { BlendMode = SKBlendMode.Screen };
    readonly ShaderSlot washShader = new(), vigShader = new();
    readonly SKPaint vig = new();
    // round 3: soft holes round the graphics (and the centre third) in the effect's own layer
    // (a soft-edged square drawn once, stretched as a nine-patch: cheap on the CPU raster too)
    readonly SKPaint hole = new() { IsAntialias = true, BlendMode = SKBlendMode.DstOut, FilterQuality = SKFilterQuality.Low };
    SKImage? holeSprite;
    static readonly SKRectI HoleCentre = new(60, 60, 100, 100);
    readonly SKPaint layerPaint = new();
    float freeK; double lastDrawMs = -1;
    /// <summary>The sky band the effect may use (design units): above this line, outside the centre third.</summary>
    public const float SkyBottom = 620f;
    /// <summary>How far (design px) the sun must be from any graphic for the effect to show fully / at all.</summary>
    public const float ClearFull = 260f, ClearNone = 150f;
    /// <summary>Last frame: how free the sun's spot was (0..1, smoothed) - harness / notes.</summary>
    public float FreeK => freeK;
    readonly float[] moteX = new float[24], moteY = new float[24], moteS = new float[24], moteP = new float[24];
    double hs, hc;           // smoothed heading (unit vector)
    bool hValid;

    /// <summary>The geometry (calibration, projection). Public for the harness / control settings.</summary>
    public readonly SunAlign Align = new();

    /// <summary>Preview / test: force the conditions (NaN = live).</summary>
    public float ForceGolden { get; set; } = float.NaN;
    public float ForceHot { get; set; } = float.NaN;
    /// <summary>Preview / test (round 3): put the sun at this screen point (design units) instead of the projected one.</summary>
    public SKPoint? ForceSun { get; set; }

    // ---- what it decided last tick (log, harness, notes)
    public CamMount Mount { get; private set; } = CamMount.Unknown;
    public double HeadingDeg { get; private set; } = double.NaN;
    public double SunAzDeg { get; private set; } = double.NaN;
    public double SunAltDeg { get; private set; } = double.NaN;
    public bool SunInView { get; private set; }
    public SKPoint SunScreen => src;
    public float Visibility => vis;

    public LensFx()
    {
        for (int i = 0; i < moteX.Length; i++)
        { moteX[i] = (float)rng.NextDouble(); moteY[i] = (float)rng.NextDouble(); moteS[i] = 0.5f + (float)rng.NextDouble(); moteP[i] = (float)rng.NextDouble() * 6.28f; }
    }

    public override bool Active(double nowMs) => vis > 0.003f || wash > 0.003f;   // round 3: no heat haze

    /// <summary>Once per tick. <paramref name="camKey"/> changes on every camera cut. <paramref name="mapKey"/> / <paramref name="map"/>
    /// = the track outline (for other cars' headings). <paramref name="on"/> = the switch.</summary>
    public void Tick(RaceSnapshot s, VpTrack? map, string mapKey, int camKey, bool on, double nowMs)
    {
        double dt = lastMs < 0 ? 16 : Math.Clamp(nowMs - lastMs, 0, 100);
        lastMs = nowMs;
        var ob = s.Onboard;
        bool live = !ReplayState.IsReplay;
        Align.SetTrack(mapKey, map);
        // learn the outline's turn to north from our own driving (live, on track)
        var me = s.PlayerOnboard;
        Align.Learn(me.LapPct, s.PlayerYawNorth, me.SpeedMs, live && s.IsConnected, nowMs);

        if (camKey != cameraKey)
        {
            cameraKey = camKey;
            Mount = SunAlign.Classify(ob.GroupOnlyName, ob.CamGroupName);
            cutAt = nowMs;
            vis = 0; wash = 0; hValid = false;           // a cut: the new shot starts clean, instantly
        }
        double az = s.SolarAzimuth, alt = s.SolarAltitude;
        SunAzDeg = az * 180 / Math.PI; SunAltDeg = alt * 180 / Math.PI;
        float sky = s.Extras.Skies switch { 0 => 1f, 1 => 0.8f, 2 => 0.35f, 3 => 0f, _ => 0.7f };
        bool carCam = Mount is CamMount.Forward or CamMount.Rearward;
        float targetVis = 0, targetWash = 0;
        SunInView = false;
        if (on && s.IsConnected && carCam && !double.IsNaN(az) && !double.IsNaN(alt) && alt > -0.6 * Math.PI / 180 && sky > 0)
        {
            bool playerLive = live && ob.CamCarIdx >= 0 && ob.CamCarIdx == s.PlayerCarIdx;
            double h = Align.Heading(playerLive, s.PlayerYawNorth, ob.LapPct, out var turn);
            bool sure = !double.IsNaN(h) && (playerLive || turn < 6 * Math.PI / 180);
            if (!double.IsNaN(h))
            {
                if (Mount == CamMount.Rearward) h += Math.PI;
                // smooth the heading a touch (a chase camera trails the car's yaw; ~120 ms)
                double k = hValid ? Math.Min(1, dt / 120.0) : 1;
                hs += (Math.Sin(h) - hs) * k; hc += (Math.Cos(h) - hc) * k; hValid = true;
            }
            if (sure && hValid)
            {
                double hh = Math.Atan2(hs, hc);
                HeadingDeg = hh * 180 / Math.PI;
                var (fov0, pitch0) = SunAlign.MountDefaults(Mount);
                float fov = Align.FovOverrideDeg > 1 ? Align.FovOverrideDeg : fov0, pitch = float.IsNaN(Align.PitchOverrideDeg) ? pitch0 : Align.PitchOverrideDeg;
                bool inView = SunAlign.Project(az, alt, hh, fov, pitch, 0.08f, out var p, out var rel);
                double altDeg = SunAltDeg;
                float low = (float)(altDeg < 2 ? 1 : altDeg < 20 ? 1 - (altDeg - 2) / 18 * 0.55 : 0.45);
                warmth = (float)(altDeg < 1 ? 1 : altDeg < 16 ? 1 - (altDeg - 1) / 15 : 0);
                if (inView) { src = p; SunInView = true; targetVis = sky * low; }
                // the warm wash only when the sun is in front of the lens (within 25 deg of the frame's edge)
                double edge = fov * Math.PI / 360 + 25 * Math.PI / 180;
                if (Math.Abs(rel) < edge)
                {
                    targetWash = sky * (0.35f + 0.65f * warmth) * (float)(1 - Math.Clamp((Math.Abs(rel) - fov * Math.PI / 360) / (25 * Math.PI / 180), 0, 1));
                    if (!inView) src = new SKPoint(rel < 0 ? -60 : 1980, Math.Clamp(p.Y, 0, 700));
                }
            }
            else HeadingDeg = double.NaN;
        }
        // after a cut: nothing for 250 ms (the camera settles), then ease in over 350 ms; leaving fades out in 250 ms
        bool settling = nowMs - cutAt < 250;
        if (!float.IsNaN(ForceGolden)) { targetVis = ForceGolden; targetWash = ForceGolden; settling = false; }
        if (ForceSun is SKPoint fs) { src = fs; SunInView = true; }
        if (settling) { targetVis = 0; targetWash = 0; }
        float up = (float)Math.Min(1, dt / 350.0), down = (float)Math.Min(1, dt / 250.0);
        vis += (targetVis - vis) * (targetVis > vis ? up : down);
        wash += (targetWash - wash) * (targetWash > wash ? up : down);
        if (vis < 0.002f && targetVis == 0) vis = 0;
        if (wash < 0.002f && targetWash == 0) wash = 0;
        float hTarget = !float.IsNaN(ForceHot) ? ForceHot : on && carCam ? Vp.Clamp01((s.TrackTempC - 38f) / 12f) * (SunAltDeg > 20 ? 1f : 0f) * sky : 0f;
        hot += (hTarget - hot) * (float)Math.Min(1, dt / 900.0);
    }

    void Ensure()
    {
        if (glow != null) return;
        glow = Sprite(512, c =>
        {
            using var p = new SKPaint { Shader = SKShader.CreateRadialGradient(new SKPoint(256, 256), 256,
                new[] { new SKColor(255, 236, 200, 255), new SKColor(255, 190, 110, 120), new SKColor(255, 140, 60, 30), new SKColor(255, 120, 40, 0) },
                new[] { 0f, 0.12f, 0.45f, 1f }, SKShaderTileMode.Clamp) };
            c.DrawCircle(256, 256, 256, p);
        });
        star = Sprite(512, c =>
        {
            using var p = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Plus };
            var rr = new Random(3);
            for (int i = 0; i < 14; i++)
            {
                float a = (float)(i * Math.PI / 7 + rr.NextDouble() * 0.1);
                float len = 120 + (float)rr.NextDouble() * 130;
                p.Shader = SKShader.CreateLinearGradient(new SKPoint(256, 256), new SKPoint(256 + MathF.Cos(a) * len, 256 + MathF.Sin(a) * len),
                    new[] { new SKColor(255, 240, 210, 150), new SKColor(255, 200, 140, 0) }, null, SKShaderTileMode.Clamp);
                p.StrokeWidth = 2.2f;
                c.DrawLine(256, 256, 256 + MathF.Cos(a) * len, 256 + MathF.Sin(a) * len, p);
                p.Shader?.Dispose(); p.Shader = null;
            }
        });
        streak = Sprite(1024, 64, c =>
        {
            using var p = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1024, 0),
                new[] { new SKColor(80, 150, 255, 0), new SKColor(140, 190, 255, 120), new SKColor(255, 236, 210, 220), new SKColor(255, 190, 120, 120), new SKColor(255, 150, 80, 0) },
                new[] { 0f, 0.3f, 0.5f, 0.7f, 1f }, SKShaderTileMode.Clamp) };
            using var m = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 64),
                new[] { SKColors.Transparent, SKColors.White, SKColors.Transparent }, null, SKShaderTileMode.Clamp), BlendMode = SKBlendMode.DstIn };
            c.DrawRect(0, 22, 1024, 20, p);
            c.DrawRect(0, 0, 1024, 64, m);
        });
        ghost = Sprite(128, c =>
        {
            using var path = new SKPath();
            for (int i = 0; i < 6; i++)
            {
                float a = i * MathF.PI / 3 + MathF.PI / 6;
                var pt = new SKPoint(64 + MathF.Cos(a) * 54, 64 + MathF.Sin(a) * 54);
                if (i == 0) path.MoveTo(pt); else path.LineTo(pt);
            }
            path.Close();
            using var f = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(64, 64), 60,
                new[] { new SKColor(255, 255, 255, 40), new SKColor(255, 255, 255, 90) }, null, SKShaderTileMode.Clamp) };
            c.DrawPath(path, f);
            using var e = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = new SKColor(255, 255, 255, 120) };
            c.DrawPath(path, e);
        });
        holeSprite = Sprite(160, c =>
        {
            using var p = new SKPaint { IsAntialias = true, Color = SKColors.Black, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 13f) };
            c.DrawRect(new SKRect(40, 40, 120, 120), p);
            using var core = new SKPaint { Color = SKColors.Black };
            c.DrawRect(new SKRect(58, 58, 102, 102), core);
        });
        mote = Sprite(32, c =>
        {
            using var p = new SKPaint { Shader = SKShader.CreateRadialGradient(new SKPoint(16, 16), 16,
                new[] { new SKColor(255, 240, 220, 200), new SKColor(255, 240, 220, 0) }, null, SKShaderTileMode.Clamp) };
            c.DrawCircle(16, 16, 16, p);
        });
        haze = Sprite(1024, 128, c =>
        {
            var rr = new Random(9);
            using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3) };
            for (int i = 0; i < 40; i++)
            {
                float y = 10 + (float)rr.NextDouble() * 108;
                p.StrokeWidth = 1 + (float)rr.NextDouble() * 2;
                p.Color = new SKColor(255, 250, 240, (byte)(14 + rr.Next(20)));
                using var path = new SKPath();
                path.MoveTo(-20, y);
                for (float x = 0; x <= 1044; x += 32) path.LineTo(x, y + MathF.Sin(x * 0.03f + i) * 3f);
                c.DrawPath(path, p);
            }
        });
    }

    static SKImage Sprite(int size, Action<SKCanvas> draw) => Sprite(size, size, draw);
    static SKImage Sprite(int w, int h, Action<SKCanvas> draw)
    {
        using var s = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        s.Canvas.Clear(SKColors.Transparent);
        draw(s.Canvas);
        return GpuContext.Pin(s.Snapshot());
    }

    /// <summary>0..1: how free the sun's spot is right now (the top band of the left / right third, clear of every graphic).</summary>
    static float FreeTarget(SKPoint p, double nowMs)
    {
        if (VpLayout.OnboardVisible) return 0f;
        // the sun just off the frame's side (the wash only) counts as the edge it is beyond
        p = new SKPoint(Math.Clamp(p.X, 0f, 1920f), Math.Max(p.Y, 0f));
        float centre = p.X < VpLayout.CentreLeft ? VpLayout.CentreLeft - p.X : p.X > VpLayout.CentreRight ? p.X - VpLayout.CentreRight : 0f;
        float k = Vp.Seg(centre, 90f, 200f) * Vp.Seg(SkyBottom - p.Y, 60f, 160f);
        if (k <= 0f) return 0f;
        return k * Vp.Seg(VpLayout.Clearance(p, nowMs), ClearNone, ClearFull);
    }

    public override void Draw(SKCanvas c, int w, int h, double nowMs)
    {
        double ddt = lastDrawMs < 0 ? 16 : Math.Clamp(nowMs - lastDrawMs, 0, 100);
        lastDrawMs = nowMs;
        // round 3: fade with the space round the sun (out fast when a graphic comes near, in when it clears)
        float ft = vis > 0.003f || wash > 0.003f ? FreeTarget(src, nowMs) : 0f;
        freeK += (ft - freeK) * (float)Math.Min(1, ddt / (ft > freeK ? 350.0 : 160.0));
        if (ft == 0f && freeK < 0.01f) freeK = 0f;
        if (!Active(nowMs)) return;
        float g = vis * freeK, wk = wash * freeK;
        if (g <= 0.003f && wk <= 0.003f) return;
        Ensure();
        double ts = nowMs / 1000.0;
        int save = c.Save();
        c.Scale(w / 1920f, h / 1080f);
        // confined to the sun's own third and the sky band; soft holes are cut round every graphic before it lands
        var zone = src.X < 960 ? new SKRect(0, 0, VpLayout.CentreLeft, SkyBottom + 80f) : new SKRect(VpLayout.CentreRight, 0, 1920, SkyBottom + 80f);
        c.ClipRect(zone);
        c.SaveLayer(zone, layerPaint);
        if (wk > 0.003f)
        {
            // warm wash from the sun's side (only when the sun is in front of the lens) and a soft amber vignette
            // the wash is centred on the sun, which moves every frame: one shader at the origin, the canvas moved to the sun
            warmWash.Shader = washShader.Radial(SKPoint.Empty, 900, [new SKColor(255, 170, 90, 90), new SKColor(255, 130, 60, 30), new SKColor(255, 120, 50, 0)], [0f, 0.45f, 1f]);
            warmWash.Color = SKColors.White.WithA(wk);
            c.Save(); c.Translate(src.X, src.Y);
            c.DrawRect(zone.Left - src.X, zone.Top - src.Y, zone.Width, zone.Height, warmWash);
            c.Restore();
            vig.Shader = vigShader.Radial(new SKPoint(960, 540), 1150, [new SKColor(40, 18, 4, 0), new SKColor(40, 18, 4, 0), new SKColor(40, 18, 4, 110)], [0f, 0.55f, 1f]);
            vig.Color = SKColors.White.WithA(0.8f * wk * warmth);
            c.DrawRect(zone, vig);
        }
        if (g > 0.003f)
        {
            // the sun: glow + starburst, breathing very slightly (heat shimmer on the lens)
            float br = 1f + 0.04f * MathF.Sin((float)(ts * 2.3));
            add.Color = SKColors.White.WithA(0.8f * g);
            float gs = 440 * br;
            c.DrawImage(glow!, new SKRect(src.X - gs / 2, src.Y - gs / 2, src.X + gs / 2, src.Y + gs / 2), add);
            c.Save(); c.Translate(src.X, src.Y); c.RotateDegrees((float)(ts * 2.0));
            add.Color = SKColors.White.WithA(0.55f * g);
            c.DrawImage(star!, new SKRect(-170, -170, 170, 170), add);
            c.Restore();
            // horizontal streak (shorter than round 2: it must not run into the graphics or the centre)
            add.Color = SKColors.White.WithA((Elevated ? 0.9f : 0.45f) * g);
            float sw = Elevated ? 900 : 560;
            c.DrawImage(streak!, new SKRect(src.X - sw / 2, src.Y - (Elevated ? 16 : 11), src.X + sw / 2, src.Y + (Elevated ? 16 : 11)), add);
            if (Elevated)
            {
                // a SHORT ghost chain: three small ghosts close to the sun, on the line toward the frame centre
                float dx = 960 - src.X, dy = 540 - src.Y;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                if (len > 1) { float k = Math.Min(1f, 260f / len); dx *= k; dy *= k; }
                ReadOnlySpan<float> at = [0.38f, 0.66f, 1.0f];
                ReadOnlySpan<float> sz = [16f, 30f, 20f];
                for (int i = 0; i < at.Length; i++)
                {
                    float gx = src.X + dx * at[i], gy = src.Y + dy * at[i];
                    var tint = i % 2 == 0 ? new SKColor(150, 200, 255) : new SKColor(255, 200, 140);
                    add.Color = tint.WithA(0.2f * g);
                    c.DrawImage(ghost!, new SKRect(gx - sz[i], gy - sz[i], gx + sz[i], gy + sz[i]), add);
                }
                // dust motes drifting through the light (only near the sun)
                for (int i = 0; i < moteX.Length; i++)
                {
                    float mx = (float)((moteX[i] * 1920 + ts * 6 * moteS[i]) % 1920), my = (float)((moteY[i] * 1080 - ts * 4 * moteS[i]) % 1080 + 1080) % 1080;
                    float d = MathF.Sqrt((mx - src.X) * (mx - src.X) + (my - src.Y) * (my - src.Y));
                    float lit = Vp.Clamp01(1f - d / 420f) * (0.5f + 0.5f * MathF.Sin((float)(ts * 1.3) + moteP[i]));
                    if (lit <= 0.02f) continue;
                    add.Color = SKColors.White.WithA(lit * g * 0.7f);
                    float ms = 4 + 6 * moteS[i];
                    c.DrawImage(mote!, new SKRect(mx - ms, my - ms, mx + ms, my + ms), add);
                }
            }
        }
        // the soft holes: every graphic on screen (grown 30 px), the centre third and below the sky band
        Span<SKRect> occ = stackalloc SKRect[24];
        int n = VpLayout.Occupied(occ, nowMs);
        for (int i = 0; i < n; i++)
        {
            var r = occ[i];
            if (r.Right < zone.Left - 80 || r.Left > zone.Right + 80 || r.Top > zone.Bottom + 80) continue;
            Hole(c, r, 30f);
        }
        Hole(c, new SKRect(VpLayout.CentreLeft, -300, VpLayout.CentreRight, 1400), 50f);
        Hole(c, new SKRect(-300, SkyBottom + 30f, 2220, 1400), 30f);
        c.RestoreToCount(save);
    }

    /// <summary>Erase <paramref name="r"/> grown by <paramref name="grow"/>, fully inside, fading to nothing ~60 px further out.</summary>
    void Hole(SKCanvas c, SKRect r, float grow)
    {
        // the sprite's solid core starts ~60 px in from its edge; the soft rim falls off over the outer ~50 px
        var d = new SKRect(r.Left - grow - 50f, r.Top - grow - 50f, r.Right + grow + 50f, r.Bottom + grow + 50f);
        c.DrawImageNinePatch(holeSprite!, HoleCentre, d, hole);
    }

    public override void Dispose()
    {
        glow?.Dispose(); star?.Dispose(); ghost?.Dispose(); streak?.Dispose(); mote?.Dispose(); haze?.Dispose();
        add.Dispose(); screen.Dispose(); hole.Dispose(); holeSprite?.Dispose(); layerPaint.Dispose(); warmWash.Dispose(); vig.Dispose(); washShader.Dispose(); vigShader.Dispose();
    }
}
