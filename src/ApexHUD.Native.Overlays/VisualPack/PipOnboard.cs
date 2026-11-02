using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>The two cars in the fight. Slot 0 = the chasing car, slot 1 = the car ahead.</summary>
public sealed record PipModel(string Title, string ChaserNum, string ChaserName, string LeaderNum, string LeaderName, string ClassName, string ClassColor);

/// <summary>
/// W10 (2026-10-27): picture-in-picture onboard during battles. iRacing draws one camera at a time and gives nothing to
/// draw a second live picture from, so the window is a live "virtual onboard" made from the telemetry iRacing sends for
/// every car: steering angle (CarIdxSteer), gear (CarIdxGear), RPM (CarIdxRPM) and the speed / gap Grandstand works out.
/// Optionally (off by default, replays / spectating only) the director cuts the MAIN camera to the chasing car's cockpit
/// while the window shows the car ahead.
/// <para>Base: a 16:9 window on the right with a broadcast frame - ONBOARD tag, class edge, car chip and name, the wheel
/// turning with the real steering, shift lights from the RPM, gear and speed, the gap to the car ahead. Elevated
/// ("duel"): a wider window with BOTH drivers' wheels side by side, lights and gears live, the gap between them, and
/// corner brackets that tighten as the gap closes under a second.</para>
/// </summary>
public sealed class PipOnboard : VpGraphic
{
    const double InMs = 450, OutMs = 380;

    PipModel? model;
    double shownAt = -1, hideAt = double.MaxValue;
    VpLayer? frame, wheel;
    bool bakedElevated;
    float fw, fh;

    // live inputs per slot (0 chaser, 1 ahead)
    readonly float[] steer = new float[2], rpm = new float[2], redline = { 9000, 9000 }, kmh = new float[2], steerShown = new float[2];
    readonly int[] gear = new int[2];
    double gap = double.NaN;

    readonly SKPaint flat = new() { IsAntialias = true };
    readonly SKPaint big = new() { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Black, TextSize = 46f, TextAlign = SKTextAlign.Center };
    readonly SKPaint mid = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 14f, TextAlign = SKTextAlign.Center };
    readonly SKPaint lbl = new() { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 9.5f, TextAlign = SKTextAlign.Center };
    readonly SKPaint gapP = new() { IsAntialias = true, Typeface = Fonts.CormorantGaramond.Bold, TextSize = 40f, TextAlign = SKTextAlign.Center };
    readonly SKPaint bracket = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, StrokeCap = SKStrokeCap.Square };

    static readonly string[] Gears = { "R", "N", "1", "2", "3", "4", "5", "6", "7", "8", "9" };

    public bool Showing => model != null && hideAt == double.MaxValue;

    public void Show(PipModel m, double nowMs)
    {
        bool fresh = model == null || hideAt != double.MaxValue || model.ChaserNum != m.ChaserNum || model.LeaderNum != m.LeaderNum;
        model = m; hideAt = double.MaxValue;
        if (fresh) shownAt = nowMs;
        Bake();
    }

    public override void Hide(double nowMs) { if (model != null && hideAt == double.MaxValue) hideAt = Math.Max(nowMs, shownAt + InMs); }

    public override bool Active(double nowMs) => model != null && nowMs < hideAt + OutMs;

    /// <summary>Live telemetry for a slot (0 = chasing car, 1 = car ahead). Steering in radians, + = left.</summary>
    public void SetCar(int slot, float steerRad, int gearNum, float engineRpm, float redlineRpm, float speedKmh)
    {
        if ((uint)slot > 1) return;
        steer[slot] = steerRad; gear[slot] = gearNum; rpm[slot] = engineRpm; kmh[slot] = speedKmh;
        if (redlineRpm > 1000) redline[slot] = redlineRpm;
    }

    public void SetGap(double seconds) => gap = seconds;

    void Bake()
    {
        frame?.Dispose(); wheel?.Dispose();
        var m = model!;
        bakedElevated = Elevated;
        fw = Elevated ? 640f : 480f; fh = Elevated ? 300f : 270f;
        var cls = Vp.ClassLine(m.ClassColor);
        frame = VpLayer.Record(new SKRect(-30, -40, fw + 30, fh + 40), c =>
        {
            var r = new SKRect(0, 0, fw, fh);
            using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 130), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 16) })
                c.DrawRect(new SKRect(r.Left, r.Top + 8, r.Right, r.Bottom + 8), sh);
            using (var bg = new SKPaint { Shader = SKShader.CreateRadialGradient(new SKPoint(fw / 2, fh * 0.62f), fw * 0.7f,
                       new[] { new SKColor(0x16, 0x1D, 0x2B), new SKColor(0x07, 0x0A, 0x10) }, null, SKShaderTileMode.Clamp) })
                c.DrawRect(r, bg);
            // faint cockpit horizon + carbon weave
            using (var wv = new SKPaint { Color = Vp.Ivory.WithAlpha(6), StrokeWidth = 1 })
                for (float x = -fh; x < fw; x += 6) c.DrawLine(x, fh, x + fh, 0, wv);
            using (var hz = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, fh * 0.45f),
                       new[] { new SKColor(0x3A, 0x55, 0x80, 70), new SKColor(0x3A, 0x55, 0x80, 0) }, null, SKShaderTileMode.Clamp) })
                c.DrawRect(new SKRect(0, 0, fw, fh * 0.45f), hz);
            using (var br = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = Vp.Ivory.WithAlpha(90) }) c.DrawRect(r, br);
            using (var tl = new SKPaint { Color = cls }) c.DrawRect(new SKRect(0, 0, fw, 3), tl);
            // the label strip above the window
            float lx = 0;
            using (var tag = new SKPaint { IsAntialias = true, Color = Vp.Ivory }) c.DrawRect(new SKRect(0, -30, 92, -4), tag);
            VpText.Draw(c, "ONBOARD", 46, -12.5f, Fonts.Inter.Bold, 11f, new SKColor(8, 10, 16), 2.2f, 0);
            lx = 104;
            using (var gl = new SKPaint { Color = new SKColor(9, 12, 18, 230) }) c.DrawRect(new SKRect(lx - 4, -30, fw, -4), gl);
            if (!Elevated)
            {
                float cw = Math.Max(32, VpText.Width(m.ChaserNum, Fonts.Inter.Bold, 12f) + 12);
                using (var cb = new SKPaint { IsAntialias = true, Color = SKColors.White }) c.DrawRoundRect(new SKRect(lx + 6, -26, lx + 6 + cw, -8), 2, 2, cb);
                VpText.Draw(c, m.ChaserNum, lx + 6 + cw / 2, -12.5f, Fonts.Inter.Bold, 12f, new SKColor(8, 10, 16), 0, 0);
                VpText.Draw(c, m.ChaserName.ToUpperInvariant(), lx + 16 + cw, -10.5f, Fonts.BarlowCondensed.Bold, 19f, Vp.Ivory, 0.6f);
                VpText.Draw(c, m.Title.ToUpperInvariant(), fw - 12, -12.5f, Fonts.Inter.Bold, 10f, Vp.Txt2, 2f, 1);
            }
            else
            {
                VpText.Draw(c, m.Title.ToUpperInvariant(), lx + 10, -12.5f, Fonts.Inter.Bold, 11f, Vp.Ivory, 2.4f);
                VpText.Draw(c, "LIVE TELEMETRY", fw - 12, -12.5f, Fonts.Inter.SemiBold, 9.5f, Vp.Txt3, 2f, 1);
                // driver plates under each wheel
                for (int s = 0; s < 2; s++)
                {
                    float cx = s == 0 ? fw * 0.25f : fw * 0.75f;
                    string num = s == 0 ? m.ChaserNum : m.LeaderNum, name = (s == 0 ? m.ChaserName : m.LeaderName).ToUpperInvariant();
                    float cw = Math.Max(30, VpText.Width(num, Fonts.Inter.Bold, 12f) + 12);
                    float nw = VpText.Width(name, Fonts.BarlowCondensed.Bold, 19f, 0.6f);
                    float x0 = cx - (cw + 8 + nw) / 2;
                    using (var cb = new SKPaint { IsAntialias = true, Color = SKColors.White }) c.DrawRoundRect(new SKRect(x0, fh - 34, x0 + cw, fh - 16), 2, 2, cb);
                    VpText.Draw(c, num, x0 + cw / 2, fh - 20.5f, Fonts.Inter.Bold, 12f, new SKColor(8, 10, 16), 0, 0);
                    VpText.Draw(c, name, x0 + cw + 8, fh - 18.5f, Fonts.BarlowCondensed.Bold, 19f, Vp.Ivory, 0.6f);
                    VpText.Draw(c, s == 0 ? "CHASING" : "AHEAD", cx, fh - 44, Fonts.Inter.SemiBold, 9.5f, Vp.Txt3, 2.2f, 0);
                }
                using (var dv = new SKPaint { Color = Vp.Ivory.WithAlpha(24) }) c.DrawRect(new SKRect(fw / 2 - 0.5f, 40, fw / 2 + 0.5f, fh - 60), dv);
            }
        });
        // the wheel (drawn once, rotated live): a GT-style rim with grips and a centre display
        float R = Elevated ? 92f : 104f;
        wheel = VpLayer.Record(new SKRect(-R - 12, -R - 12, R + 12, R + 12), c => Wheel(c, R));
    }

    static void Wheel(SKCanvas c, float R)
    {
        using var rim = new SKPaint { IsAntialias = true, Color = new SKColor(0x22, 0x26, 0x2E) };
        using var path = new SKPath();
        // flat-bottom rim
        path.AddRoundRect(new SKRect(-R, -R * 0.78f, R, R * 0.66f), R * 0.42f, R * 0.42f);
        using var inner = new SKPath();
        inner.AddRoundRect(new SKRect(-R * 0.74f, -R * 0.56f, R * 0.74f, R * 0.44f), R * 0.3f, R * 0.3f);
        using var ring = path.Op(inner, SKPathOp.Difference);
        if (ring != null)
        {
            c.DrawPath(ring, rim);
            using var hi = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = Vp.Ivory.WithAlpha(60) };
            c.DrawPath(ring, hi);
        }
        // grips (alcantara)
        using var grip = new SKPaint { IsAntialias = true, Color = new SKColor(0x10, 0x12, 0x16) };
        c.DrawRoundRect(new SKRect(-R, -R * 0.36f, -R * 0.74f, R * 0.3f), 6, 6, grip);
        c.DrawRoundRect(new SKRect(R * 0.74f, -R * 0.36f, R, R * 0.3f), 6, 6, grip);
        // centre plate + display
        using var plate = new SKPaint { IsAntialias = true, Color = new SKColor(0x18, 0x1B, 0x22) };
        c.DrawRoundRect(new SKRect(-R * 0.62f, -R * 0.5f, R * 0.62f, R * 0.4f), 10, 10, plate);
        using var disp = new SKPaint { IsAntialias = true, Color = new SKColor(0x03, 0x05, 0x08) };
        c.DrawRoundRect(new SKRect(-R * 0.4f, -R * 0.38f, R * 0.4f, R * 0.14f), 4, 4, disp);
        // buttons
        var cols = new[] { new SKColor(0xE5, 0x23, 0x2F), new SKColor(0x3B, 0x82, 0xF6), new SKColor(0xFF, 0xB4, 0x1F), new SKColor(0x34, 0xD3, 0x99) };
        for (int i = 0; i < 4; i++)
        {
            using var b = new SKPaint { IsAntialias = true, Color = cols[i].WithAlpha(200) };
            float bx = (i < 2 ? -1 : 1) * R * 0.52f, by = (i % 2 == 0 ? -0.18f : 0.18f) * R;
            c.DrawCircle(bx, by, R * 0.055f, b);
        }
        // top marker
        using var mk = new SKPaint { Color = Vp.GoldHi };
        c.DrawRect(new SKRect(-3, -R * 0.78f, 3, -R * 0.66f), mk);
    }

    public override void Draw(SKCanvas c, int w, int h, double nowMs)
    {
        Vp.LastScale = Vp.DeviceScale(c);
        var m = model;
        if (m == null || frame == null || wheel == null) return;
        if (bakedElevated != Elevated) Bake();
        if (nowMs >= hideAt + OutMs) { model = null; return; }
        double t = nowMs - shownAt;
        float inK = Vp.EaseOutExpo(Vp.Seg(t, 0, InMs)), outK = Vp.EaseIn(Vp.Seg(nowMs, hideAt, hideAt + OutMs));
        float a = Vp.Seg(t, 0, 180) * (1f - outK);
        float x = w - 40f - fw + (1f - inK) * 60f + outK * 60f, y = Math.Max(Elevated ? 400f : 420f, VpLayout.CardTop);   // #413
        VpLayout.Claim(VpLayout.SlotPip, new SKRect(w - 40f - fw, y, w - 40f, y + fh), nowMs);   // round 3
        int save = c.Save();
        c.Translate(x, y);
        // grow out of a line (like a monitor switching on)
        float open = Vp.EaseOutExpo(Vp.Seg(t, 60, 420));
        c.Save();
        c.ClipRect(new SKRect(-40, fh / 2 - (fh / 2 + 50) * open, fw + 40, fh / 2 + (fh / 2 + 50) * open));
        frame.Draw(c, 0, 0, a);
        // steering smoothing (the sim sends 60 Hz; ease it so it reads as a hand on the wheel)
        for (int s = 0; s < 2; s++) steerShown[s] += (steer[s] - steerShown[s]) * 0.35f;
        if (!Elevated) Cockpit(c, 0, fw / 2, fh * 0.58f, a, true);
        else
        {
            Cockpit(c, 0, fw * 0.25f, fh * 0.47f, a, false);
            Cockpit(c, 1, fw * 0.75f, fh * 0.47f, a, false);
            if (!double.IsNaN(gap))
            {
                gapP.Color = Vp.Ivory.WithA(a);
                VpNum.Draw(c, gap, 2, fw / 2, fh * 0.5f + 12, gapP);
                lbl.Color = Vp.Txt3.WithA(a);
                TextBlobs.Draw(c, "GAP", fw / 2, fh * 0.5f - 26, lbl);
            }
            // brackets tighten inside one second
            float tight = double.IsNaN(gap) ? 0f : Vp.Clamp01((float)(1.2 - gap));
            float inset = 10f + 8f * tight * (0.6f + 0.4f * MathF.Sin((float)(t / 200.0)));
            bracket.Color = Vp.Mix(Vp.Ivory, Vp.Amber, tight).WithA(a);
            float L = 22;
            c.DrawLine(inset, inset, inset + L, inset, bracket); c.DrawLine(inset, inset, inset, inset + L, bracket);
            c.DrawLine(fw - inset, inset, fw - inset - L, inset, bracket); c.DrawLine(fw - inset, inset, fw - inset, inset + L, bracket);
            c.DrawLine(inset, fh - inset, inset + L, fh - inset, bracket); c.DrawLine(inset, fh - inset, inset, fh - inset - L, bracket);
            c.DrawLine(fw - inset, fh - inset, fw - inset - L, fh - inset, bracket); c.DrawLine(fw - inset, fh - inset, fw - inset, fh - inset - L, bracket);
        }
        if (!Elevated && !double.IsNaN(gap))
        {
            // gap plate bottom-right
            flat.Color = new SKColor(9, 12, 18, 220).WithA(a);
            c.DrawRect(new SKRect(fw - 128, fh - 50, fw - 10, fh - 10), flat);
            lbl.Color = Vp.Txt3.WithA(a);
            lbl.TextAlign = SKTextAlign.Left;
            TextBlobs.Draw(c, "TO CAR AHEAD", fw - 120, fh - 36, lbl);
            lbl.TextAlign = SKTextAlign.Center;
            mid.Color = Vp.Ivory.WithA(a); mid.TextSize = 17f; mid.TextAlign = SKTextAlign.Left;
            VpNum.Draw(c, gap, 2, fw - 120, fh - 16, mid, signed: false);
            mid.TextAlign = SKTextAlign.Center; mid.TextSize = 14f;
        }
        c.Restore();
        // the opening line
        if (open < 1f)
        {
            flat.Color = Vp.Ivory.WithA(a * (1f - open));
            c.DrawRect(new SKRect(0, fh / 2 - 1, fw, fh / 2 + 1), flat);
        }
        c.RestoreToCount(save);
    }

    void Cockpit(SKCanvas c, int s, float cx, float cy, float a, bool single)
    {
        float R = Elevated ? 92f : 104f;
        // shift lights: 10 LEDs across the top, green -> red -> blue at the limit
        float f = redline[s] > 0 ? rpm[s] / redline[s] : 0f;
        int n = 10;
        float lw = R * 1.5f, x0 = cx - lw / 2, ly = cy - R - 14;
        bool flash = f >= 0.97f && ((int)(Environment.TickCount64 / 90) % 2 == 0);
        for (int i = 0; i < n; i++)
        {
            float th = 0.62f + 0.35f * i / (n - 1);
            bool on = f >= th;
            var col = i < 4 ? Vp.Gain : i < 7 ? Vp.Amber : Vp.LiveRed;
            if (flash) col = new SKColor(0x3B, 0x82, 0xF6);
            flat.Color = (on || flash ? col : new SKColor(40, 44, 52)).WithA(a);
            c.DrawCircle(x0 + lw * i / (n - 1), ly, 4.2f, flat);
        }
        c.Save();
        c.Translate(cx, cy);
        c.RotateDegrees(-steerShown[s] * 180f / MathF.PI);
        wheel!.Draw(c, 0, 0, a);
        // gear + speed on the wheel display (rotates with it, like the real one)
        int gi = Math.Clamp(gear[s] + 1, 0, Gears.Length - 1);
        big.Color = Vp.Ivory.WithA(a); big.TextSize = R * 0.36f;
        TextBlobs.Draw(c, Gears[gi], 0, -R * 0.07f, big);
        mid.Color = Vp.GoldHi.WithA(a); mid.TextSize = R * 0.12f;
        if (kmh[s] > 0) VpNum.Draw(c, kmh[s], 0, 0, R * 0.1f, mid);
        mid.TextSize = 14f;
        c.Restore();
    }

    public override void Dispose()
    {
        frame?.Dispose(); wheel?.Dispose();
        foreach (var p in new[] { flat, big, mid, lbl, gapP, bracket }) p.Dispose();
    }
}
