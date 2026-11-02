using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// 2026-10-24 (Angelo: "anytime the car is in an onboard I want a very refined throttle, brake, steering, speed and
/// gear - fit our luxurious theme, handcrafted, not AI"). A small glass instrument in the bottom-left corner:
/// a slim dial (gear in the serif numerals, RPM as a fine gold arc that warms toward the shift point, a gold
/// steering mark that turns with the wheel) beside the speed, a four-second pedal trace and two hairline
/// throttle / brake bars. Other cars' onboards only get what iRacing sends for them (steering, gear, RPM,
/// speed from lap progress), shown as a centred steering bar instead of pedals.
/// Render thread only; paints and strings are kept between frames.
/// </summary>
public sealed class OnboardRenderer
{
    public const float W = 548f, H = 192f;

    static readonly SKColor Glass = Hex(BroadcastPalette.TowerGlassBase, 222);
    static readonly SKColor Ivory = Hex(BroadcastPalette.TextPrimary);
    static readonly SKColor Tert = Hex(BroadcastPalette.TextTertiary);
    static readonly SKColor Gold = Hex(BroadcastPalette.Leader);
    static readonly SKColor Amber = new(0xF2, 0xB0, 0x5A);
    static readonly SKColor Crimson = new(0xE0, 0x31, 0x4B);
    static readonly SKColor Sage = new(0x9F, 0xD8, 0xB0);

    readonly SKPaint fill = new() { IsAntialias = true };
    readonly SKPaint stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
    readonly SKPaint text = new() { IsAntialias = true };
    readonly SKPath tracePath = new(), markPath = new();
    readonly ShaderSlot glowSlot = new(), thrSlot = new(), brkSlot = new(), sheenSlot = new();

    // pedal history for the trace: (time s, throttle, brake), ring buffer
    const int HistN = 400;
    readonly double[] hT = new double[HistN]; readonly float[] hThr = new float[HistN], hBrk = new float[HistN];
    int hHead, hCount;
    // smoothed values (the snapshot ticks at 60 Hz, presents can be uneven)
    float sThr, sBrk, sSteer, sRpm, sSpeed; int lastCar = -2;

    // strings made once per value
    int spdVal = -1; string spdTxt = ""; int gearVal = int.MinValue; string gearTxt = "";
    int thrVal = -1; string thrTxt = ""; int brkVal = -1; string brkTxt = "";
    int nameCar = -2; string nameTxt = "", numTxt = ""; SKColor classCol = Gold;

    double shownSinceMs = -1;

    public void Draw(SKCanvas c, float left, float bottom, RaceSnapshot snap, double nowMs, float alpha)
    {
        var ob = snap.Onboard;
        if (alpha <= 0.01f || ob.CamCarIdx < 0) return;
        if (shownSinceMs < 0 || alpha < 0.02f) shownSinceMs = nowMs;
        double t = nowMs / 1000.0;
        if (ob.CamCarIdx != lastCar) { lastCar = ob.CamCarIdx; hCount = 0; sThr = ob.Throttle; sBrk = ob.Brake; sSteer = ob.SteerRad; sRpm = ob.Rpm; sSpeed = ob.SpeedMs; }
        const float k = 0.45f;
        sThr += (ob.Throttle - sThr) * k; sBrk += (ob.Brake - sBrk) * k; sSteer += (ob.SteerRad - sSteer) * k;
        sRpm += (ob.Rpm - sRpm) * 0.35f; sSpeed += (ob.SpeedMs - sSpeed) * 0.3f;
        if (ob.HasPedals) Push(t, sThr, sBrk);
        Identify(snap, ob.CamCarIdx);

        var r = new SKRect(left, bottom - H, left + W, bottom);
        int save = c.Save();
        c.Translate(0, (1 - alpha) * 16f);

        // ---- glass, warm bloom from the dial side, gold top hairline, class spine
        fill.Shader = null; fill.Color = Glass.WithAlpha((byte)(Glass.Alpha * alpha)); c.DrawRect(r, fill);
        fill.Shader = glowSlot.Radial(new SKPoint(r.Left + 92f, r.Top + 114f), 200f,
            [Gold.WithAlpha(34), Gold.WithAlpha(8), Gold.WithAlpha(0)], [0f, 0.5f, 1f]);
        fill.Color = SKColors.White.WithAlpha((byte)(255 * alpha)); c.DrawRect(r, fill); fill.Shader = null;
        fill.Shader = sheenSlot.Linear(new SKPoint(r.Left, 0), new SKPoint(r.Right, 0), [Gold.WithAlpha(0), Gold.WithAlpha(200), Gold.WithAlpha(0)], [0f, 0.35f, 1f]);
        c.DrawRect(new SKRect(r.Left, r.Top, r.Right, r.Top + 1.2f), fill); fill.Shader = null;
        fill.Color = classCol.WithAlpha((byte)(255 * alpha)); c.DrawRect(new SKRect(r.Left, r.Top, r.Left + 3f, r.Bottom), fill);

        // ---- header: ONBOARD · NAME ............ [29]
        float hy = r.Top + 25f;
        text.Typeface = Fonts.Inter.Bold; text.TextSize = 13f; text.TextAlign = SKTextAlign.Left; text.Color = Gold.WithAlpha((byte)(255 * alpha));   // #340: 13 (was 11)
        TextTracked.Draw(c, text, "ONBOARD", r.Left + 20f, hy, 1.6f);
        text.Typeface = Fonts.Inter.SemiBold; text.TextSize = 14f; text.Color = Ivory.WithAlpha((byte)(235 * alpha));
        TextTracked.Draw(c, text, nameTxt, r.Left + 108f, hy, 1.6f);
        if (numTxt.Length > 0)
        {
            text.Typeface = Fonts.Inter.Bold; text.TextSize = 13f;
            float nw = text.MeasureText(numTxt) + 14f;
            var chip = new SKRect(r.Right - 18f - nw, hy - 14f, r.Right - 18f, hy + 5f);
            fill.Color = SKColors.White.WithAlpha((byte)(240 * alpha)); c.DrawRect(chip, fill);
            text.Color = new SKColor(8, 10, 16, (byte)(242 * alpha)); text.TextAlign = SKTextAlign.Center;
            TextTracked.Draw(c, text, numTxt, chip.MidX, hy, 0f);
            text.TextAlign = SKTextAlign.Left;
        }
        fill.Color = new SKColor(255, 255, 255, (byte)(14 * alpha));
        c.DrawRect(new SKRect(r.Left + 20f, r.Top + 36f, r.Right - 18f, r.Top + 37f), fill);

        // ---- dial
        float cx = r.Left + 92f, cy = r.Top + 114f, R = 56f;
        DrawDial(c, cx, cy, R, ob, alpha, nowMs);

        // ---- speed
        float x0 = r.Left + 178f, x1 = r.Right - 20f;
        int spd = (int)Math.Round(sSpeed * (ob.Metric ? 3.6f : 2.23694f));
        if (spd < 0) spd = 0;
        if (spd != spdVal) { spdVal = spd; spdTxt = spd.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        text.Typeface = Fonts.CormorantGaramond.Bold; text.TextSize = 50f; text.Color = Ivory.WithAlpha((byte)(255 * alpha));
        TextTracked.Draw(c, text, spdTxt, x0, r.Top + 84f, 0f);
        float sw = text.MeasureText(spdTxt);
        text.Typeface = Fonts.Inter.SemiBold; text.TextSize = 13f; text.Color = Tert.WithAlpha((byte)(255 * alpha));   // #340: was 10.5
        TextTracked.Draw(c, text, ob.Metric ? "KM/H" : "MPH", x0 + sw + 10f, r.Top + 82f, 1.6f);

        if (ob.HasPedals)
        {
            DrawTrace(c, new SKRect(x0 + 152f, r.Top + 52f, x1, r.Top + 88f), t, alpha);
            DrawBar(c, x0, x1, r.Top + 136f, "THROTTLE", sThr, thrSlot, Ivory, Sage, ref thrVal, ref thrTxt, alpha);
            DrawBar(c, x0, x1, r.Top + 168f, "BRAKE", sBrk, brkSlot, new SKColor(0xFF, 0x6A, 0x7C), Crimson, ref brkVal, ref brkTxt, alpha);
        }
        else
            DrawSteerBar(c, x0, x1, r.Top + 150f, ob, alpha);

        c.RestoreToCount(save);
    }

    void DrawDial(SKCanvas c, float cx, float cy, float R, OnboardData ob, float a, double nowMs)
    {
        const float start = 135f, sweep = 270f;
        var arcR = new SKRect(cx - R, cy - R, cx + R, cy + R);
        // track + ticks
        stroke.Shader = null; stroke.StrokeWidth = 3f; stroke.Color = new SKColor(255, 255, 255, (byte)(22 * a));
        c.DrawArc(arcR, start, sweep, false, stroke);
        stroke.StrokeWidth = 1f; stroke.Color = new SKColor(255, 255, 255, (byte)(60 * a));
        for (int i = 0; i <= 9; i++)
        {
            double ang = (start + sweep * i / 9.0) * Math.PI / 180;
            float c0 = (float)Math.Cos(ang), s0 = (float)Math.Sin(ang);
            float rin = R - (i % 3 == 0 ? 9f : 6f);
            c.DrawLine(cx + c0 * rin, cy + s0 * rin, cx + c0 * (R - 3f), cy + s0 * (R - 3f), stroke);
        }
        // RPM: gold, warming to amber toward the shift point, crimson pulse past it
        // 2026-10-29 (car parity): as the full onboard - the camera car's own shift rpm; none known = no warm-up / pulse
        bool shiftKnown = ob.ShiftRpm > 1000;
        float red = ob.RedlineRpm > 1000 ? ob.RedlineRpm
                  : shiftKnown ? (MathF.Floor(ob.ShiftRpm / 1000f) + 1f) * 1000f
                  : Math.Max(9000f, sRpm * 1.05f);
        float shift = shiftKnown ? ob.ShiftRpm : float.MaxValue;
        float f = Math.Clamp(sRpm / red, 0f, 1f);
        SKColor rc = Gold;
        if (sRpm > shift * 0.9f) rc = Lerp(Gold, Amber, Math.Clamp((sRpm - shift * 0.9f) / (shift * 0.1f), 0f, 1f));
        if (sRpm >= shift) { float p = (float)(0.6 + 0.4 * Math.Sin(nowMs / 1000.0 * Math.PI * 2 * 5)); rc = Lerp(Amber, Crimson, p); }
        stroke.StrokeWidth = 3f; stroke.Color = rc.WithAlpha((byte)(255 * a));
        if (f > 0.002f) c.DrawArc(arcR, start, sweep * f, false, stroke);
        // inner ring + steering mark (turns with the wheel; iRacing's angle is + to the left)
        float ri = R - 15f;
        stroke.StrokeWidth = 1f; stroke.Color = new SKColor(255, 255, 255, (byte)(26 * a));
        c.DrawCircle(cx, cy, ri, stroke);
        float deg = (float)(sSteer * 180.0 / Math.PI);
        c.Save();
        c.Translate(cx, cy); c.RotateDegrees(-deg);
        markPath.Reset();
        markPath.MoveTo(0, -ri - 4.5f); markPath.LineTo(-4.2f, -ri + 3.5f); markPath.LineTo(4.2f, -ri + 3.5f); markPath.Close();
        fill.Shader = null; fill.Color = Gold.WithAlpha((byte)(255 * a)); c.DrawPath(markPath, fill);
        stroke.Color = Gold.WithAlpha((byte)(70 * a)); stroke.StrokeWidth = 1.2f;
        c.DrawArc(new SKRect(-ri, -ri, ri, ri), -90f - 18f, 36f, false, stroke);   // a short lit segment either side of the mark
        c.Restore();
        // gear
        int g = ob.Gear;
        if (g != gearVal) { gearVal = g; gearTxt = g < 0 ? "R" : g == 0 ? "N" : g.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        text.Typeface = Fonts.CormorantGaramond.Bold; text.TextSize = 52f; text.TextAlign = SKTextAlign.Center;
        text.Color = Ivory.WithAlpha((byte)(255 * a));
        TextTracked.Draw(c, text, gearTxt, cx, cy + 18f, 0f);
        text.TextAlign = SKTextAlign.Left;
    }

    void DrawBar(SKCanvas c, float x0, float x1, float y, string label, float v, ShaderSlot slot, SKColor c0, SKColor c1,
                 ref int lastVal, ref string lastTxt, float a)
    {
        text.Typeface = Fonts.Inter.SemiBold; text.TextSize = 12.5f; text.Color = Tert.WithAlpha((byte)(255 * a));   // #340: was 9.5
        TextTracked.Draw(c, text, label, x0, y - 9f, 1.5f);
        int pct = (int)Math.Round(Math.Clamp(v, 0f, 1f) * 100f);
        if (pct != lastVal) { lastVal = pct; lastTxt = pct.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        text.Typeface = Fonts.Inter.SemiBold; text.TextSize = 13f; text.Color = Ivory.WithAlpha((byte)(240 * a)); text.TextAlign = SKTextAlign.Right;
        TextTracked.Draw(c, text, lastTxt, x1, y + 4f, 0.4f);
        text.TextAlign = SKTextAlign.Left;
        float bx0 = x0, bx1 = x1 - 34f;
        var track = new SKRect(bx0, y - 2f, bx1, y + 2f);
        fill.Shader = null; fill.Color = new SKColor(255, 255, 255, (byte)(20 * a)); c.DrawRoundRect(track, 2f, 2f, fill);
        float fx = bx0 + (bx1 - bx0) * Math.Clamp(v, 0f, 1f);
        if (fx > bx0 + 1f)
        {
            fill.Shader = slot.Linear(new SKPoint(bx0, 0), new SKPoint(bx1, 0), [c0, c1], []);
            fill.Color = SKColors.White.WithAlpha((byte)(255 * a));
            c.DrawRoundRect(new SKRect(bx0, y - 2f, fx, y + 2f), 2f, 2f, fill);
            fill.Shader = null;
            fill.Color = c1.WithAlpha((byte)(255 * a)); c.DrawCircle(fx, y, 3.2f, fill);   // a bead at the leading edge
        }
    }

    void DrawSteerBar(SKCanvas c, float x0, float x1, float y, OnboardData ob, float a)
    {
        text.Typeface = Fonts.Inter.SemiBold; text.TextSize = 12.5f; text.Color = Tert.WithAlpha((byte)(255 * a));   // #340: was 9.5
        TextTracked.Draw(c, text, "STEERING", x0, y - 12f, 1.5f);
        float mid = (x0 + x1 - 34f) / 2f, half = (x1 - 34f - x0) / 2f;
        fill.Shader = null; fill.Color = new SKColor(255, 255, 255, (byte)(20 * a));
        c.DrawRoundRect(new SKRect(x0, y - 2f, x1 - 34f, y + 2f), 2f, 2f, fill);
        fill.Color = new SKColor(255, 255, 255, (byte)(70 * a)); c.DrawRect(new SKRect(mid - 0.5f, y - 6f, mid + 0.5f, y + 6f), fill);
        float max = ob.SteerMaxRad > 0.5f ? ob.SteerMaxRad : 4.7f;   // ~270 deg each way
        float u = Math.Clamp(-sSteer / max, -1f, 1f);               // + = right on screen
        float px = mid + u * half;
        fill.Color = Gold.WithAlpha((byte)(200 * a));
        c.DrawRoundRect(new SKRect(Math.Min(mid, px), y - 2f, Math.Max(mid, px), y + 2f), 2f, 2f, fill);
        fill.Color = Gold.WithAlpha((byte)(255 * a)); c.DrawCircle(px, y, 3.4f, fill);
    }

    void Push(double t, float thr, float brk)
    {
        if (hCount > 0 && t - hT[(hHead - 1 + HistN) % HistN] < 0.012) return;
        hT[hHead] = t; hThr[hHead] = thr; hBrk[hHead] = brk;
        hHead = (hHead + 1) % HistN; if (hCount < HistN) hCount++;
    }

    void DrawTrace(SKCanvas c, SKRect box, double now, float a)
    {
        const double span = 4.0;
        fill.Shader = null; fill.Color = new SKColor(255, 255, 255, (byte)(12 * a));
        c.DrawRect(new SKRect(box.Left, box.Bottom, box.Right, box.Bottom + 1f), fill);
        c.DrawRect(new SKRect(box.Left, box.Top, box.Right, box.Top + 0.6f), fill);
        for (int pass = 0; pass < 2; pass++)
        {
            tracePath.Reset(); bool started = false;
            for (int i = hCount - 1; i >= 0; i--)
            {
                int idx = (hHead - 1 - i + HistN * 2) % HistN;
                double age = now - hT[idx];
                if (age > span) continue;
                float x = box.Right - (float)(age / span) * box.Width;
                float v = pass == 0 ? hThr[idx] : hBrk[idx];
                float y = box.Bottom - v * box.Height;
                if (!started) { tracePath.MoveTo(x, y); started = true; } else tracePath.LineTo(x, y);
            }
            if (!started) continue;
            stroke.Shader = null; stroke.StrokeWidth = 1.4f;
            stroke.Color = (pass == 0 ? Sage : Crimson).WithAlpha((byte)((pass == 0 ? 210 : 230) * a));
            c.DrawPath(tracePath, stroke);
        }
    }

    void Identify(RaceSnapshot snap, int car)
    {
        if (car == nameCar) return;
        nameCar = car; nameTxt = ""; numTxt = ""; classCol = Gold;
        foreach (var cls in snap.ClassLeaderboard)
            foreach (var d in cls.Drivers)
                if (d.CarIdx == car)
                {
                    nameTxt = NameFormat.Broadcast(d).ToUpperInvariant();
                    numTxt = d.CarNumber ?? "";
                    var col = string.IsNullOrEmpty(d.ClassColor) ? BroadcastPalette.Class(d.ClassName) : d.ClassColor;
                    classCol = Hex(col);
                    if (classCol.Red + classCol.Green + classCol.Blue < 60) classCol = Ivory;
                    return;
                }
    }

    static SKColor Lerp(SKColor x, SKColor y, float u) => new(
        (byte)(x.Red + (y.Red - x.Red) * u), (byte)(x.Green + (y.Green - x.Green) * u), (byte)(x.Blue + (y.Blue - x.Blue) * u), 255);

    static SKColor Hex(string hex, byte alpha = 255)
    {
        var s = hex.TrimStart('#');
        if (s.Length != 6) return new SKColor(255, 255, 255, alpha);
        return new SKColor(Convert.ToByte(s[..2], 16), Convert.ToByte(s[2..4], 16), Convert.ToByte(s[4..6], 16), alpha);
    }
}
