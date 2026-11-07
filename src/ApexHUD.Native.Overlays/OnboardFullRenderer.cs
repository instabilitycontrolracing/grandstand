using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// 2026-10-24 (Angelo: "it should take up the full screen, not just be a little card"): the onboard look as a
/// full broadcast treatment - no box. Pieces sit around the picture's edges so the shot stays clear:
/// a wide shallow RPM arc across the bottom with shift lights, the gear large in the middle, speed left,
/// RPM right, a minimal steering wheel that turns with the real one, tall hairline brake / throttle bars
/// framing the cluster, a full-width pedal ribbon along the bottom edge, a driver plate top-left and the
/// gaps top-right. Laid out for 1920x1080 (the host scales it up for 4K). Render thread only.
/// </summary>
public sealed partial class OnboardFullRenderer
{
    static readonly SKColor Ivory = Hex(BroadcastPalette.TextPrimary);
    static readonly SKColor Tert = Hex(BroadcastPalette.TextTertiary);
    static readonly SKColor Gold = Hex(BroadcastPalette.Leader);
    static readonly SKColor Amber = new(0xF2, 0xB0, 0x5A);
    static readonly SKColor Crimson = new(0xE0, 0x31, 0x4B);
    static readonly SKColor Sage = new(0x9F, 0xD8, 0xB0);
    // pedal colours (Angelo 2026-10-24: "the throttle side should be green and the brake side red")
    static readonly SKColor GreenLo = new(0x1F, 0x9D, 0x55), GreenHi = new(0x5C, 0xE6, 0x8E);
    static readonly SKColor RedLo = new(0xA8, 0x16, 0x2B), RedHi = new(0xFF, 0x4A, 0x5F);

    readonly SKPaint fill = new() { IsAntialias = true };
    readonly SKPaint stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
    readonly SKPaint text = new() { IsAntialias = true };
    readonly SKPath path = new();
    readonly ShaderSlot vigBottom = new(), vigTop = new(), barThr = new(), barBrk = new(), plateRule = new();

    const int HistN = 900;
    readonly double[] hT = new double[HistN]; readonly float[] hThr = new float[HistN], hBrk = new float[HistN];
    readonly float[] hPct = new float[HistN], hDelta = new float[HistN]; readonly bool[] hDOk = new bool[HistN];
    int hHead, hCount;

    // best-lap ghost (Angelo 2026-10-24: "place our best lap trace with it to show where we are slower"): the
    // player's pedals recorded by track position for the lap in progress, the last full lap, and the best lap.
    const int Bins = 1500;
    float[] curThr = new float[Bins], curBrk = new float[Bins], lastThr = new float[Bins], lastBrk = new float[Bins];
    readonly float[] bestThr = new float[Bins], bestBrk = new float[Bins];
    bool[] curHas = new bool[Bins], lastHas = new bool[Bins];
    bool haveBest, lastFull; int lastBin = -1; double promoteUntil = -1; int binSession = int.MinValue;
    float dShown; int dVal = int.MinValue; string dTxt = "";
    readonly ShaderSlot lossSlot = new();
    SKBitmap? classLogo; string? classLogoFor;
    float sThr, sBrk, sSteer, sRpm, sSpeed; int lastCar = -2;

    string? gearBoundsFor; SKRect gearBounds;
    int spdVal = -1; string spdTxt = ""; int rpmVal = -1; string rpmTxt = ""; int gearVal = int.MinValue; string gearTxt = "";
    int thrVal = -1; string thrTxt = ""; int brkVal = -1; string brkTxt = "";
    int idCar = -2, idPos = -1, idLap = -1; string nameTxt = "", numTxt = "", subTxt = "", posTxt = "", classTxt = "", lapTxt = "";
    string aheadTxt = "", behindTxt = "", aheadName = "", behindName = ""; double gapsAt = -1;

    float fThr, fBrk;

    // the best-lap ghost is kept on disk (2026-10-25: a Grandstand restart used to lose it until the next best lap)
    string ghostKey = "";
    static readonly string GhostPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "onboard_ghost.json");
    void SaveGhost()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new { key = ghostKey, thr = bestThr, brk = bestBrk });
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(GhostPath)!);
            System.IO.File.WriteAllText(GhostPath + ".tmp", json);
            System.IO.File.Move(GhostPath + ".tmp", GhostPath, true);
        }
        catch { /* a nicety */ }
    }
    void LoadGhost()
    {
        try
        {
            if (!System.IO.File.Exists(GhostPath)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(GhostPath));
            var r = doc.RootElement;
            if (r.GetProperty("key").GetString() != ghostKey) return;
            var t = r.GetProperty("thr"); var b = r.GetProperty("brk");
            if (t.GetArrayLength() != Bins || b.GetArrayLength() != Bins) return;
            int i = 0; foreach (var v in t.EnumerateArray()) bestThr[i++] = v.GetSingle();
            i = 0; foreach (var v in b.EnumerateArray()) bestBrk[i++] = v.GetSingle();
            haveBest = true;
        }
        catch { }
    }

    /// <summary>Every frame, whether the onboard view is up or not (2026-10-25, Angelo: it should be "populated already,
    /// not just when the camera is on it"): our own car's pedal trace, the best-lap ghost, the g trail and this lap's
    /// gain / loss around the map keep recording, so the view opens full.</summary>
    public void Feed(RaceSnapshot snap, double nowMs)
    {
        var pb = snap.PlayerOnboard;
        double t = nowMs / 1000.0;
        if (snap.SessionNum != binSession) { binSession = snap.SessionNum; ResetBins(); hCount = 0; Array.Clear(lapHas); lapBin = -1; ghostKey = ""; }
        string gk = snap.SessionKey + ":" + snap.TrackRawName + ":" + snap.PlayerCarIdx;
        if (gk != ghostKey) { ghostKey = gk; LoadGhost(); }
        if (snap.Player is { } me) MaybePromote(me, t);
        if (!pb.IsPlayerCar || !pb.HasPedals) return;
        fThr += (pb.Throttle - fThr) * 0.45f; fBrk += (pb.Brake - fBrk) * 0.45f;
        Push(t, fThr, fBrk, pb.LapPct, pb.DeltaBest, pb.DeltaOk);
        Record(pb.LapPct, pb.Throttle, pb.Brake, t);
        if (pb.HasG) { sLat += (pb.LatAccel - sLat) * 0.35f; sLong += (pb.LongAccel - sLong) * 0.35f; PushG(t, sLat, sLong); }
        if (pb.LapPct >= 0)
        {
            int b = Math.Clamp((int)(pb.LapPct * MapBins), 0, MapBins - 1);
            if (lapBin >= 0 && b < lapBin - MapBins / 2) Array.Clear(lapHas);       // new lap
            if (pb.DeltaOk) { lapDelta[b] = pb.DeltaBest; lapHas[b] = true; }
            lapBin = b;
        }
    }

    public void Draw(SKCanvas c, float W, float H, RaceSnapshot snap, double nowMs, float alpha)
    {
        var ob = snap.Onboard;
        if (alpha <= 0.01f || ob.CamCarIdx < 0) return;
        double t = nowMs / 1000.0;
        if (ob.CamCarIdx != lastCar) { lastCar = ob.CamCarIdx; sThr = ob.Throttle; sBrk = ob.Brake; sSteer = ob.SteerRad; sRpm = ob.Rpm; sSpeed = ob.SpeedMs; gapsAt = -1; }
        sThr += (ob.Throttle - sThr) * 0.45f; sBrk += (ob.Brake - sBrk) * 0.45f; sSteer += (ob.SteerRad - sSteer) * 0.45f;
        sRpm += (ob.Rpm - sRpm) * 0.35f; sSpeed += (ob.SpeedMs - sSpeed) * 0.3f;
        Identify(snap, ob.CamCarIdx, t);

        byte A(float v) => (byte)Math.Clamp(v * 255f * alpha, 0, 255);
        float cx = W / 2f;

        // ---- soft fades so the graphics read over bright scenery
        fill.Shader = vigBottom.Linear(new SKPoint(0, H - 330), new SKPoint(0, H), [new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 120), new SKColor(0, 0, 0, 190)], [0f, 0.55f, 1f]);
        fill.Color = SKColors.White.WithAlpha(A(1)); c.DrawRect(0, H - 330, W, 330, fill);
        fill.Shader = vigTop.Linear(new SKPoint(0, 0), new SKPoint(0, 200), [new SKColor(0, 0, 0, 120), new SKColor(0, 0, 0, 0)], []);
        c.DrawRect(0, 0, W, 200, fill); fill.Shader = null;

        // ---- driver plate, top-left
        float px = 64f, py = 74f;
        text.Typeface = Fonts.Inter.Bold; text.TextSize = 14f; text.TextAlign = SKTextAlign.Left; text.Color = Gold.WithAlpha(A(1));   // #340: was 12
        TextTracked.DrawHalo(c, text, "ONBOARD", px, py, 2.6f);
        text.Typeface = Fonts.CormorantGaramond.Bold; text.TextSize = 44f; text.Color = Ivory.WithAlpha(A(1));
        TextTracked.Draw(c, text, nameTxt, px, py + 44f, 0.6f);
        float nw = Math.Max(220f, text.MeasureText(nameTxt));
        fill.Shader = plateRule.Linear(new SKPoint(px, 0), new SKPoint(px + nw + 40f, 0), [Gold.WithAlpha(220), Gold.WithAlpha(0)], []);
        fill.Color = SKColors.White.WithAlpha(A(1)); c.DrawRect(px, py + 58f, nw + 40f, 1.2f, fill); fill.Shader = null;
        float sx = px;
        if (numTxt.Length > 0)
        {
            text.Typeface = Fonts.Inter.Bold; text.TextSize = 14f;
            float cw = text.MeasureText(numTxt) + 14f;
            fill.Color = SKColors.White.WithAlpha(A(0.95f)); c.DrawRect(sx, py + 70f, cw, 22f, fill);
            text.Color = new SKColor(8, 10, 16, A(0.95f)); text.TextAlign = SKTextAlign.Center;
            TextTracked.Draw(c, text, numTxt, sx + cw / 2f, py + 86f, 0f); text.TextAlign = SKTextAlign.Left;
            sx += cw + 12f;
        }
        // P2   [class logo]   LAP 7  (Angelo 2026-10-24: "the GTP should be the GTP logo")
        text.Typeface = Fonts.Inter.SemiBold; text.TextSize = 14.5f; text.Color = Ivory.WithAlpha(A(0.95f));   // #340: was 13 @0.85
        if (posTxt.Length > 0) { TextTracked.DrawHalo(c, text, posTxt, sx, py + 86f, 2.0f); sx += text.MeasureText(posTxt) + 2f * posTxt.Length + 14f; }
        if (classLogo != null && classLogo.Height > 0)
        {
            float lh = 15f, lw = classLogo.Width * lh / classLogo.Height;
            logoPaint.Color = SKColors.White.WithAlpha(A(1));
            c.DrawBitmap(classLogo, new SKRect(sx, py + 74.5f, sx + lw, py + 74.5f + lh), logoPaint);
            sx += lw + 14f;
        }
        else if (classTxt.Length > 0) { TextTracked.DrawHalo(c, text, classTxt, sx, py + 86f, 2.0f); sx += text.MeasureText(classTxt) + 2f * classTxt.Length + 14f; }
        if (lapTxt.Length > 0)
        {
            fill.Color = Ivory.WithAlpha(A(0.5f)); c.DrawCircle(sx - 7f, py + 81.5f, 1.5f, fill);
            TextTracked.DrawHalo(c, text, lapTxt, sx + 4f, py + 86f, 2.0f);
        }

        // ---- the cars either side (2026-10-25, Angelo: "show the ahead / behind as the car cards, like a smaller version
        // of the battle cards ... a very transparent battle card of the car behind middle-left, the car ahead middle-right")
        if (mapBehindD != null && behindTxt.Length > 0) SideCard(c, 64f, H * 0.19f, mapBehindD, "BEHIND", behindTxt, alpha);   // up, clear of the radio card (Angelo 17:08)
        if (mapAheadD != null && aheadTxt.Length > 0) SideCard(c, W - 64f - SideW, H * 0.19f, mapAheadD, "AHEAD", aheadTxt, alpha);

        // ---- the tach: a flat bar (2026-10-25, Angelo: "the tach should be flat not curved")
        // 2026-10-29 (car parity): the shift rpm is the camera car's own (iRacing's for our model, Lovely Car Data's for
        // others). Known shift but no redline: the scale runs to the next thousand past it. Neither known: a plain
        // scale with no shift lights and no red zone - never a made-up shift point.
        // 2026-11-06 (#876): the tach and shift lights only on his own car (other cars: no gauge in the way)
        if (ob.IsPlayerCar)
        {
        bool shiftKnown = ob.ShiftRpm > 1000;
        float red = ob.RedlineRpm > 1000 ? ob.RedlineRpm
                  : shiftKnown ? (MathF.Floor(ob.ShiftRpm / 1000f) + 1f) * 1000f
                  : Math.Max(9000f, sRpm * 1.05f);
        float shift = shiftKnown ? ob.ShiftRpm : float.MaxValue;
        float tl = cx - TachHalf, tr = cx + TachHalf, ty = H - 290f;
        fill.Shader = null; fill.Color = new SKColor(255, 255, 255, A(0.12f));
        c.DrawRoundRect(new SKRect(tl, ty - 2f, tr, ty + 2f), 2f, 2f, fill);
        int kmax = (int)Math.Floor(red / 1000f);
        stroke.Shader = null; stroke.StrokeWidth = 1.2f;
        text.Typeface = Fonts.Inter.SemiBold; text.TextSize = 13f; text.TextAlign = SKTextAlign.Center;   // #340: was 11 @0.9
        for (int k = 0; k <= kmax; k++)
        {
            float x = tl + (tr - tl) * (k * 1000f / red);
            bool hot = k * 1000f >= shift - 1;
            stroke.Color = (hot ? Crimson : SKColors.White).WithAlpha(A(hot ? 0.8f : 0.45f));
            c.DrawLine(x, ty - 7f, x, ty - 15f, stroke);
            text.Color = (hot ? Crimson : Tert).WithAlpha(A(1f));
            TextTracked.DrawHalo(c, text, Digits(k), x, ty - 22f, 0f);
        }
        float f = Math.Clamp(sRpm / red, 0f, 1f);
        SKColor rc = Gold;
        if (shiftKnown && sRpm > shift * 0.9f) rc = Lerp(Gold, Amber, Math.Clamp((sRpm - shift * 0.9f) / (shift * 0.1f), 0f, 1f));
        if (sRpm >= shift) rc = Lerp(Amber, Crimson, (float)(0.6 + 0.4 * Math.Sin(t * Math.PI * 10)));
        if (f > 0.002f)
        {
            float fx = tl + (tr - tl) * f;
            fill.Color = rc.WithAlpha(A(0.25f)); c.DrawRoundRect(new SKRect(tl, ty - 5f, fx, ty + 5f), 4f, 4f, fill);   // soft glow
            fill.Color = rc.WithAlpha(A(1)); c.DrawRoundRect(new SKRect(tl, ty - 2.5f, fx, ty + 2.5f), 2.5f, 2.5f, fill);
        }

        // shift lights: nine small jewels above the tach
        float ly = ty - 50f;
        for (int i = 0; i < (shiftKnown ? 9 : 0); i++)
        {
            float lx = cx + (i - 4) * 22f;
            // 2026-10-25 (Angelo: "the shift lights approach the middle from the outside, not left to right"): both ends
            // first, closing in pair by pair to the centre LED, green outside -> amber -> red in the middle
            int st = Math.Min(i, 8 - i);
            float on = Math.Clamp((sRpm - (shift * 0.84f + shift * 0.16f * st / 4f)) / 150f, 0f, 1f);
            SKColor lc = st < 2 ? Sage : st < 3 ? Amber : Crimson;
            if (sRpm >= shift && (int)(t * 10) % 2 == 0) lc = Crimson;
            fill.Color = new SKColor(255, 255, 255, A(0.12f)); c.DrawCircle(lx, ly, 4.2f, fill);
            if (on > 0) { fill.Color = lc.WithAlpha(A(on)); c.DrawCircle(lx, ly, 4.2f, fill); }
        }
        }

        // TC and ABS / LOCK lights either side of the wheel (2026-10-25): lit while traction control is cutting the throttle
        // and while ABS is working - or, on a car without ABS, while a wheel is locked - our own car only (iRacing doesn't send it for others)
        if (ob.HasAbsTc)
        {
            kTc = ob.TcActive ? 1f : Math.Max(0f, kTc - 0.08f);
            kAbs = ob.AbsActive ? 1f : Math.Max(0f, kAbs - 0.08f);
            double nowS = Environment.TickCount64 / 1000.0;
            if (ob.TcActive) { if (tcSince < 0) tcSince = nowS; } else tcSince = -1;
            if (ob.AbsActive) { if (lockSince < 0) { lockSince = nowS; lockRear = ob.Brake < 0.2f; } } else lockSince = -1;
            // 2026-10-25 (Angelo: "the little rounded light pods on the dash of the M Hybrid V8"): the BMW GTP manual -
            // two LED clusters on the dash, the left one for the left wheels, the right one for the right wheels.
            // Wheelspin / TC: every LED blue, flashing for a little intervention, solid blue when it's heavy.
            // Lockup: pink for a front wheel, yellow for a rear, more LEDs the worse it is.
            // We don't get each wheel from iRacing, so: the lockup grows with how long it lasts, the wheels on the inside
            // of the turn lock first, and a lockup with the brake pedal (nearly) off is the rears (engine braking on a downshift).
            float tcHeld = tcSince < 0 ? 0f : (float)(nowS - tcSince), lockHeld = lockSince < 0 ? 0f : (float)(nowS - lockSince);
            int sev = kAbs < 0.5f ? 0 : lockHeld < 0.12f ? 1 : lockHeld < 0.3f ? 2 : 3;
            int sevL = sev, sevR = sev;
            if (sev > 0 && Math.Abs(sSteer) > 0.06f) { if (sSteer > 0) sevR = Math.Max(1, sev - 1); else sevL = Math.Max(1, sev - 1); }   // + = turning left
            float kT = ob.CarHasTc ? kTc : 0f;
            bool tcSolid = tcHeld > 0.4f;
            AidLight(c, cx - 116f, H - 226f, kT, tcSolid, sevL, lockRear, alpha);
            AidLight(c, cx + 116f, H - 226f, kT, tcSolid, sevR, lockRear, alpha);
        }

        // 2026-11-06 (#912, Angelo): the steering wheel and the speed only on his own car - other cars get the map there
        if (ob.IsPlayerCar)
        {
        // ---- steering wheel (turns with the real one; iRacing's angle is + to the left)
        float wy = H - 226f, wr = 38f;
        c.Save(); c.Translate(cx, wy); c.RotateDegrees((float)(-sSteer * 180.0 / Math.PI));
        stroke.StrokeWidth = 2f; stroke.Color = Ivory.WithAlpha(A(0.85f));
        path.Reset();
        path.AddArc(new SKRect(-wr, -wr, wr, wr), 200f, 140f);            // the top rim
        path.MoveTo(-wr * 0.94f, wr * 0.34f); path.LineTo(-wr * 0.55f, wr * 0.70f); path.LineTo(wr * 0.55f, wr * 0.70f); path.LineTo(wr * 0.94f, wr * 0.34f);   // flat bottom
        c.DrawPath(path, stroke);
        stroke.StrokeWidth = 1.4f; stroke.Color = Ivory.WithAlpha(A(0.55f));
        c.DrawLine(-wr * 0.9f, -wr * 0.08f, -wr * 0.28f, wr * 0.05f, stroke); c.DrawLine(wr * 0.9f, -wr * 0.08f, wr * 0.28f, wr * 0.05f, stroke);
        c.DrawCircle(0, wr * 0.08f, wr * 0.28f, stroke);
        fill.Color = Gold.WithAlpha(A(1)); c.DrawRect(-2f, -wr - 4f, 4f, 9f, fill);   // the 12 o'clock stripe
        c.Restore();
        }

        // ---- gear, speed, rpm
        int g = ob.Gear;
        if (g != gearVal) { gearVal = g; gearTxt = g < 0 ? "R" : g == 0 ? "N" : Digits(g); }
        text.Typeface = Fonts.CormorantGaramond.Bold; text.TextSize = 118f; text.TextAlign = SKTextAlign.Center; text.Color = Ivory.WithAlpha(A(1));
        // 2026-10-24 (Angelo: "in certain gears the number touches the trace"): the serif's old-style figures
        // hang below the line (3 4 5 7 9), so each gear is centred on its own shape - every gear sits at the
        // same height, clear of the ribbon
        if (gearBoundsFor != gearTxt) { gearBoundsFor = gearTxt; text.MeasureText(gearTxt, ref gearBounds); }
        // 2026-11-06 (#927, Angelo): the gear number only on his own car - other cars' onboards get a bigger map there instead
        if (ob.IsPlayerCar)
            TextTracked.Draw(c, text, gearTxt, cx, H - 138f - (gearBounds.Top + gearBounds.Bottom) / 2f, 0f);
        if (ob.IsPlayerCar)
        {
        int spd = Math.Max(0, (int)Math.Round(sSpeed * (ob.Metric ? 3.6f : 2.23694f)));
        if (spd != spdVal) { spdVal = spd; spdTxt = Digits(spd); }
        text.TextSize = 66f; text.TextAlign = SKTextAlign.Right;
        // 2026-10-25 (Angelo): white below 150 mph, gold from 150 mph, blinking gold from 200 mph (241 / 322 km/h)
        float mph = sSpeed * 2.23694f;
        if (mph >= 200f) text.Color = (int)(t * 2.5) % 2 == 0 ? Gold.WithAlpha(A(1)) : Gold.WithAlpha(A(0.35f));
        else if (mph >= 150f) text.Color = Gold.WithAlpha(A(1));
        TextTracked.Draw(c, text, spdTxt, cx - 150f, H - 92f, 0f);
        text.Color = Ivory.WithAlpha(A(1));
        text.Typeface = Fonts.Inter.SemiBold; text.TextSize = 13f; text.Color = Tert.WithAlpha(A(1));   // #340: was 11
        TextTracked.DrawHalo(c, text, ob.Metric ? "KM/H" : "MPH", cx - 150f, H - 70f, 2.0f);
        }
        // 2026-11-06 (#876, Angelo): the RPM number only on his own car - on other cars it just sits in the way
        if (ob.IsPlayerCar)
        {
        int rpm = (int)(Math.Round(sRpm / 10f) * 10);
        if (rpm != rpmVal) { rpmVal = rpm; rpmTxt = rpm.ToString("N0", CultureInfo.InvariantCulture); }
        text.Typeface = Fonts.CormorantGaramond.Bold; text.TextSize = 50f; text.TextAlign = SKTextAlign.Left; text.Color = Ivory.WithAlpha(A(0.92f));
        TextTracked.Draw(c, text, rpmTxt, cx + 150f, H - 92f, 0f);
        text.Typeface = Fonts.Inter.SemiBold; text.TextSize = 13f; text.Color = Tert.WithAlpha(A(1));
        TextTracked.DrawHalo(c, text, "RPM", cx + 150f, H - 70f, 2.0f);
        }
        text.TextAlign = SKTextAlign.Left;

        // ---- pedals: tall hairline bars framing the cluster + the ribbon along the bottom edge
        if (ob.HasPedals)
        {
            VBar(c, cx - PedalX, H - 86f, 136f, sBrk, barBrk, RedLo, RedHi, ref brkVal, ref brkTxt, alpha);
            VBar(c, cx + PedalX, H - 86f, 136f, sThr, barThr, GreenLo, GreenHi, ref thrVal, ref thrTxt, alpha);
            Ribbon(c, new SKRect(64f, H - 54f, W - 64f, H - 20f), t, alpha);
            if (ob.DeltaOk) DeltaReadout(c, W - 64f, H - 66f, ob.DeltaBest, alpha);
        }
        if (ob.HasG)
        {
            // g-circle, bottom-left (Angelo 2026-10-24: "the g circle or the traction circle")
            GCircle(c, 230f, H - 196f, 82f, t, alpha);
        }
        // mini track map, bottom-right - opposite the g-circle (Angelo 2026-10-25: "the word")
        if (ob.IsPlayerCar) MiniMap(c, W - 250f, H - 196f, snap, ob, alpha, nowMs);
        else
        {   // #912: in the middle, where the wheel was; #927: 1.6x bigger (no wheel, no gear, no speed there now)
            const float big = 1.6f; float my = H - 210f;
            c.Save(); c.Scale(big, big, cx, my);
            MiniMap(c, cx, my, snap, ob, alpha, nowMs);
            c.Restore();
        }
        PlaceLabel(c, W, snap, ob, alpha, nowMs);
    }

    const float TachHalf = 330f, PedalX = 372f;   // pedal bars sit just outside the tach (Angelo: "closer to it")
    float kTc, kAbs;
    double tcSince = -1, lockSince = -1; bool lockRear;

    // the GTP dash colours (BMW M Hybrid V8 manual): blue = wheelspin / TC, pink = front lockup, yellow = rear lockup
    static readonly SKColor PodTc = new(0x2F, 0x7D, 0xFF), PodFront = new(0xFF, 0x5F, 0xC8), PodRear = new(0xFF, 0xD2, 0x2E);

    /// <summary>One of the dash's rounded LED pods: a row of LEDs for the front wheel over a row for the rear.</summary>
    void AidLight(SKCanvas c, float x, float y, float kTc, bool tcSolid, int lockSev, bool lockRear, float a)
    {
        byte A(float v) => (byte)Math.Clamp(v * 255f * a, 0, 255);
        const int n = 3;
        const float cell = 11f, rad = 3.9f;
        float pw = n * cell + 14f, ph = 2 * cell + 14f;
        var r = new SKRect(x - pw / 2f, y - ph / 2f - 6f, x + pw / 2f, y + ph / 2f - 6f);
        bool flashOn = ((int)(Environment.TickCount64 / 70) & 1) == 0;                     // light TC flickers
        float tcLit = kTc < 0.5f ? kTc * 0.6f : tcSolid ? 1f : flashOn ? 1f : 0.15f;
        fill.Shader = null;
        // glow behind the pod in the colour that's lit
        SKColor? glow = tcLit > 0.3f ? PodTc : lockSev > 0 ? (lockRear ? PodRear : PodFront) : null;
        if (glow is { } g)
        {
            fill.Color = g.WithAlpha(A(0.35f)); fill.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 9f);
            c.DrawRoundRect(new SKRect(r.Left - 5, r.Top - 5, r.Right + 5, r.Bottom + 5), ph / 2f, ph / 2f, fill);
            fill.MaskFilter = null;
        }
        fill.Color = new SKColor(5, 6, 8, A(0.94f)); c.DrawRoundRect(r, ph / 2f, ph / 2f, fill);           // rounded pod housing
        stroke.Shader = null; stroke.StrokeWidth = 1f; stroke.Color = Ivory.WithAlpha(A(0.16f)); c.DrawRoundRect(r, ph / 2f, ph / 2f, stroke);
        for (int row = 0; row < 2; row++)                                                      // row 0 = front, row 1 = rear
            for (int i = 0; i < n; i++)
            {
                float lx = r.Left + 7f + cell * (i + 0.5f), ly2 = r.Top + 7f + cell * (row + 0.5f);
                SKColor col; float lit;
                if (tcLit > 0.05f) { col = PodTc; lit = tcLit; }                                 // spin: the whole pod blue
                else
                {
                    bool thisRow = (row == 1) == lockRear;
                    col = row == 0 ? PodFront : PodRear;
                    lit = thisRow && i < lockSev ? 1f : 0f;
                }
                fill.Color = new SKColor(255, 255, 255, A(0.06f)); c.DrawCircle(lx, ly2, rad, fill);   // unlit lens
                if (lit > 0.01f)
                {
                    fill.Color = col.WithAlpha(A(0.4f * lit)); c.DrawCircle(lx, ly2, rad * 1.75f, fill);
                    fill.Color = Lerp(col, SKColors.White, 0.3f).WithAlpha(A(lit)); c.DrawCircle(lx, ly2, rad, fill);
                }
            }
        string label = tcLit > 0.05f ? "TC" : lockSev > 0 ? "LOCK" : "";
        if (label.Length > 0)
        {
            text.Typeface = Fonts.Inter.Bold; text.TextSize = 13f; text.TextAlign = SKTextAlign.Center;   // #340: was 11
            text.Color = (glow ?? Ivory).WithAlpha(A(1));
            TextTracked.DrawHalo(c, text, label, x, r.Bottom + 17f, 1.6f);
            text.TextAlign = SKTextAlign.Left;
        }
    }

    Driver? mapAheadD, mapBehindD;
    const float SideW = 300f, SideH = 214f;

    /// <summary>A see-through battle-card column for the car either side: AHEAD / BEHIND and the gap, the real car
    /// (iRacing's picture with its livery), the name and position. Very transparent so the onboard view shows through.</summary>
    void SideCard(SKCanvas c, float x, float y, Driver d, string label, string gap, float alpha)
    {
        var r = new SKRect(x, y, x + SideW, y + SideH);
        using var lp = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(255 * alpha * 0.78f)) };
        int layer = c.SaveLayer(r, lp);
        fill.Shader = null;
        fill.Color = new SKColor(0x0B, 0x0E, 0x14, 92); c.DrawRect(r, fill);                     // glass, very light
        fill.Color = Gold.WithAlpha(150); c.DrawRect(new SKRect(r.Left, r.Top, r.Right, r.Top + 2f), fill);
        text.TextAlign = SKTextAlign.Left;
        text.Typeface = Fonts.Inter.Bold; text.TextSize = 13f; text.Color = Gold;   // #340: was 11
        TextTracked.DrawHalo(c, text, label, r.Left + 16f, r.Top + 24f, 2.4f);
        text.Typeface = Fonts.CormorantGaramond.Bold; text.TextSize = 40f; text.Color = Ivory; text.TextAlign = SKTextAlign.Right;
        c.DrawText(gap, r.Right - 16f, r.Top + 42f, text);
        text.TextAlign = SKTextAlign.Left;
        if (LiveryArt.Get(d) is { } car)
        {
            float cw = SideW - 60f, ch = cw * car.Height / car.Width;
            if (ch > 88f) { ch = 88f; cw = ch * car.Width / car.Height; }
            var dst = new SKRect(r.MidX - cw / 2f, r.Top + 52f + (88f - ch), r.MidX + cw / 2f, r.Top + 140f);
            using var ip = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            c.DrawImage(car, dst, ip);
        }
        text.Typeface = Fonts.CormorantGaramond.Bold; text.TextSize = 26f; text.Color = Ivory;
        string nm = NameFormat.Broadcast(d).ToUpperInvariant();
        while (text.MeasureText(nm) > SideW - 32f && text.TextSize > 14) text.TextSize -= 1f;
        TextTracked.Draw(c, text, nm, r.Left + 16f, r.Top + 174f, 0.6f);
        text.Typeface = Fonts.Inter.Bold; text.TextSize = 13f; text.Color = new SKColor(0xDA, 0xDE, 0xE4);   // #340: was 11, C9CED6
        string sub = (d.PositionInClass > 0 ? $"P{d.PositionInClass}" : "") + (string.IsNullOrEmpty(d.CarNumber) ? "" : $"   ·   #{d.CarNumber}") + (string.IsNullOrEmpty(d.CarName) ? "" : $"   ·   {d.CarName.ToUpperInvariant()}");
        while (text.MeasureText(sub) + 1.2f * sub.Length > SideW - 32f && sub.Contains("·")) sub = sub[..sub.LastIndexOf("   ·")];
        TextTracked.DrawHalo(c, text, sub, r.Left + 16f, r.Top + 199f, 1.2f);
        c.RestoreToCount(layer);
    }

    void GapRow(SKCanvas c, float right, float y, string label, string gap, string name, float a)
    {
        text.Typeface = Fonts.CormorantGaramond.Bold; text.TextSize = 30f; text.Color = Ivory.WithAlpha((byte)(255 * a));
        TextTracked.Draw(c, text, gap, right, y, 0f);
        float gw = text.MeasureText(gap);
        text.Typeface = Fonts.Inter.SemiBold; text.TextSize = 14f; text.Color = Ivory.WithAlpha((byte)(240 * a));   // #340: was 12 @200
        TextTracked.DrawHalo(c, text, name, right - gw - 16f, y - 2f, 1.3f);
        float nw = text.MeasureText(name) + 1.3f * Math.Max(0, name.Length - 1);
        text.Typeface = Fonts.Inter.Bold; text.TextSize = 13f; text.Color = Gold.WithAlpha((byte)(255 * a));   // #340: was 10.5
        TextTracked.DrawHalo(c, text, label, right - gw - 16f - nw - 14f, y - 2f, 1.8f);
    }

    void VBar(SKCanvas c, float x, float bottom, float h, float v, ShaderSlot slot, SKColor c0, SKColor c1,
              ref int lastVal, ref string lastTxt, float a)
    {
        byte A(float k) => (byte)Math.Clamp(k * 255f * a, 0, 255);
        float top = bottom - h;
        fill.Shader = null; fill.Color = new SKColor(255, 255, 255, A(0.12f));
        c.DrawRoundRect(new SKRect(x - 2.5f, top, x + 2.5f, bottom), 2.5f, 2.5f, fill);
        float fy = bottom - h * Math.Clamp(v, 0f, 1f);
        if (fy < bottom - 1f)
        {
            fill.Shader = slot.Linear(new SKPoint(0, bottom), new SKPoint(0, top), [c0, c1], []);
            fill.Color = SKColors.White.WithAlpha(A(1));
            c.DrawRoundRect(new SKRect(x - 2.5f, fy, x + 2.5f, bottom), 2.5f, 2.5f, fill);
            fill.Shader = null; fill.Color = c1.WithAlpha(A(1)); c.DrawCircle(x, fy, 4.5f, fill);
        }
        int pct = (int)Math.Round(Math.Clamp(v, 0f, 1f) * 100f);
        if (pct != lastVal) { lastVal = pct; lastTxt = Digits(pct); }
        text.Typeface = Fonts.CormorantGaramond.Bold; text.TextSize = 30f; text.TextAlign = SKTextAlign.Center; text.Color = Ivory.WithAlpha(A(0.95f));
        TextTracked.Draw(c, text, lastTxt, x, top - 14f, 0f);
        text.TextAlign = SKTextAlign.Left;
    }

    void Push(double t, float thr, float brk, float pct, float delta, bool dok)
    {
        if (hCount > 0 && t - hT[(hHead - 1 + HistN) % HistN] < 0.012) return;
        hT[hHead] = t; hThr[hHead] = thr; hBrk[hHead] = brk; hPct[hHead] = pct; hDelta[hHead] = delta; hDOk[hHead] = dok;
        hHead = (hHead + 1) % HistN; if (hCount < HistN) hCount++;
    }

    void Ribbon(SKCanvas c, SKRect box, double now, float a)
    {
        const double span = 8.0;
        fill.Shader = null; fill.Color = new SKColor(255, 255, 255, (byte)(18 * a));
        c.DrawRect(new SKRect(box.Left, box.Bottom, box.Right, box.Bottom + 1f), fill);

        // where time goes: a faint red wash where we lose time to the best lap, green where we gain
        int prev = -1; float colX = float.NaN, colRate = 0; int colN = 0; float colStart = 0;
        for (int i = hCount - 1; i >= 0; i--)
        {
            int idx = (hHead - 1 - i + HistN * 2) % HistN;
            double age = now - hT[idx];
            if (age > span || !hDOk[idx]) { prev = -1; continue; }
            float x = box.Right - (float)(age / span) * box.Width;
            if (prev >= 0)
            {
                double dt = hT[idx] - hT[prev];
                if (dt > 0)
                {
                    float rate = (float)((hDelta[idx] - hDelta[prev]) / dt);      // s lost per s
                    if (float.IsNaN(colX)) { colX = x; colStart = x; colRate = 0; colN = 0; }
                    colRate += rate; colN++;
                    if (x - colStart >= 6f)
                    {
                        Wash(c, colStart, x, box, colRate / colN, a);
                        colStart = x; colRate = 0; colN = 0;
                    }
                }
            }
            prev = idx;
        }

        // the best lap's pedals at the same places round the track (ghost), then ours
        for (int pass = 0; pass < 4; pass++)
        {
            bool ghost = pass < 2;
            if (ghost && !haveBest) continue;
            bool brake = pass % 2 == 1;
            path.Reset(); bool started = false;
            for (int i = hCount - 1; i >= 0; i--)
            {
                int idx = (hHead - 1 - i + HistN * 2) % HistN;
                double age = now - hT[idx];
                if (age > span) continue;
                float v;
                if (ghost)
                {
                    float p = hPct[idx];
                    if (p < 0) { started = false; continue; }
                    int b = Math.Clamp((int)(p * Bins), 0, Bins - 1);
                    v = brake ? bestBrk[b] : bestThr[b];
                }
                else v = brake ? hBrk[idx] : hThr[idx];
                if (brake && v < 0.01f) { started = false; continue; }   // brake only drawn while pressed (no red rule along the bottom)
                float x = box.Right - (float)(age / span) * box.Width;
                float y = box.Bottom - v * box.Height;
                if (!started) { path.MoveTo(x, y); started = true; } else path.LineTo(x, y);
            }
            stroke.Shader = null;
            if (ghost)
            {
                stroke.StrokeWidth = 1.2f;
                stroke.Color = brake ? Gold.WithAlpha((byte)(170 * a)) : SKColors.White.WithAlpha((byte)(120 * a));
            }
            else
            {
                stroke.StrokeWidth = 1.6f;
                stroke.Color = (brake ? RedHi : GreenHi).WithAlpha((byte)((brake ? 235 : 215) * a));
            }
            c.DrawPath(path, stroke);
        }
    }

    // ---- g-circle
    const float G = 9.80665f, GMax = 3.5f;
    float sLat, sLong;
    const int GN = 90;
    readonly double[] gT = new double[GN]; readonly float[] gX = new float[GN], gY = new float[GN];
    int gHead, gCount; float gPeak; double gPeakAt; int gVal = -1; string gTxt = "";

    void PushG(double t, float lat, float lng)
    {
        if (gCount > 0 && t - gT[(gHead - 1 + GN) % GN] < 0.015) return;
        // the dot goes where the force pushes the driver: braking -> up, right-hander -> left
        gT[gHead] = t; gX[gHead] = lat / G; gY[gHead] = lng / G;
        gHead = (gHead + 1) % GN; if (gCount < GN) gCount++;
        float tot = MathF.Sqrt(lat * lat + lng * lng) / G;
        if (tot > gPeak || t - gPeakAt > 2.0) { gPeak = tot; gPeakAt = t; }
    }

    void GCircle(SKCanvas c, float cx, float cy, float R, double now, float a)
    {
        byte A(float k) => (byte)Math.Clamp(k * 255f * a, 0, 255);
        float k = R / GMax;
        // a faint disc so it reads over the picture, rings at 1 / 2 / 3 g, a hairline cross
        fill.Shader = null; fill.Color = new SKColor(0, 0, 0, A(0.28f)); c.DrawCircle(cx, cy, R + 6f, fill);
        stroke.Shader = null; stroke.StrokeWidth = 1f;
        for (int gi = 1; gi <= 3; gi++)
        {
            stroke.Color = new SKColor(255, 255, 255, A(gi == 3 ? 0.30f : 0.16f));
            c.DrawCircle(cx, cy, gi * k, stroke);
        }
        stroke.Color = new SKColor(255, 255, 255, A(0.12f));
        c.DrawLine(cx - R, cy, cx + R, cy, stroke); c.DrawLine(cx, cy - R, cx, cy + R, stroke);
        text.Typeface = Fonts.Inter.SemiBold; text.TextSize = 11.5f; text.TextAlign = SKTextAlign.Left; text.Color = Tert.WithAlpha(A(0.95f));   // #340: was 8.5 @0.8
        for (int gi = 1; gi <= 3; gi++) TextTracked.Draw(c, text, small[gi] + "G", cx + gi * k * 0.7071f + 2f, cy - gi * k * 0.7071f - 2f, 0.6f);
        // the last second as a fading trail, then the dot
        for (int i = gCount - 1; i >= 0; i--)
        {
            int idx = (gHead - 1 - i + GN * 2) % GN;
            double age = now - gT[idx];
            if (age > 1.0) continue;
            float u = 1f - (float)age;
            float x = cx + Math.Clamp(gX[idx], -GMax, GMax) * k, y = cy + Math.Clamp(gY[idx], -GMax, GMax) * k;
            fill.Color = Gold.WithAlpha(A(0.35f * u * u)); c.DrawCircle(x, y, 1.6f + 1.4f * u, fill);
        }
        float dx = cx + Math.Clamp(sLat / G, -GMax, GMax) * k, dy = cy + Math.Clamp(sLong / G, -GMax, GMax) * k;
        fill.Color = Gold.WithAlpha(A(0.25f)); c.DrawCircle(dx, dy, 9f, fill);
        fill.Color = Gold.WithAlpha(A(1f)); c.DrawCircle(dx, dy, 4.6f, fill);
        // combined g (peak held two seconds) under the circle
        float tot = MathF.Sqrt(sLat * sLat + sLong * sLong) / G;
        int v = (int)Math.Round(tot * 10f);
        if (v != gVal) { gVal = v; gTxt = (v / 10f).ToString("0.0", CultureInfo.InvariantCulture); }
        text.Typeface = Fonts.CormorantGaramond.Bold; text.TextSize = 30f; text.TextAlign = SKTextAlign.Right; text.Color = Ivory.WithAlpha(A(1));
        TextTracked.Draw(c, text, gTxt, cx + 4f, cy + R + 40f, 0f);
        text.Typeface = Fonts.Inter.SemiBold; text.TextSize = 13f; text.TextAlign = SKTextAlign.Left; text.Color = Tert.WithAlpha(A(1));   // #340: was 10
        TextTracked.DrawHalo(c, text, "G", cx + 10f, cy + R + 38f, 2f);
        text.TextAlign = SKTextAlign.Left;
    }

    void Wash(SKCanvas c, float x0, float x1, SKRect box, float rate, float a)
    {
        float k = Math.Clamp(Math.Abs(rate) / 0.25f, 0f, 1f);
        if (k < 0.08f) return;
        var col = rate > 0 ? RedHi : GreenHi;
        fill.Shader = null; fill.Color = col.WithAlpha((byte)(46 * k * a));
        c.DrawRect(new SKRect(x0, box.Top - 6f, x1, box.Bottom), fill);
    }

    void DeltaReadout(SKCanvas c, float right, float y, float delta, float a)
    {
        dShown += (delta - dShown) * 0.25f;
        int v = (int)Math.Round(dShown * 100f);
        if (v != dVal) { dVal = v; dTxt = (v > 0 ? "+" : v < 0 ? "\u2212" : "") + (Math.Abs(v) / 100f).ToString("0.00", CultureInfo.InvariantCulture); }
        var col = v > 0 ? RedHi : v < 0 ? GreenHi : Ivory;
        text.Typeface = Fonts.CormorantGaramond.Bold; text.TextSize = 30f; text.TextAlign = SKTextAlign.Right; text.Color = col.WithAlpha((byte)(255 * a));
        TextTracked.Draw(c, text, dTxt, right, y, 0f);
        float w = text.MeasureText(dTxt);
        text.Typeface = Fonts.Inter.SemiBold; text.TextSize = 13f; text.Color = Tert.WithAlpha((byte)(255 * a));   // #340: was 10
        TextTracked.DrawHalo(c, text, "VS BEST LAP", right - w - 12f, y - 2f, 1.6f);
        text.TextAlign = SKTextAlign.Left;
    }

    void ResetBins()
    {
        Array.Clear(curHas); Array.Clear(lastHas); haveBest = false; lastFull = false; lastBin = -1; promoteUntil = -1;
    }

    /// <summary>Pedals by track position for the lap in progress; at the line it becomes "the last lap".</summary>
    void Record(float pct, float thr, float brk, double t)
    {
        if (pct < 0) return;
        int b = Math.Clamp((int)(pct * Bins), 0, Bins - 1);
        if (lastBin >= 0 && b < lastBin - Bins / 2)
        {
            // crossed the line: keep the lap if we saw nearly all of it (a replay jump or pit stop leaves holes)
            int have = 0; for (int i = 0; i < Bins; i++) if (curHas[i]) have++;
            (lastThr, curThr) = (curThr, lastThr); (lastBrk, curBrk) = (curBrk, lastBrk); (lastHas, curHas) = (curHas, lastHas);
            Array.Clear(curHas);
            lastFull = have > Bins * 0.9;
            promoteUntil = lastFull ? t + 12 : -1;
            lastBin = -1;
        }
        if (lastBin >= 0 && b > lastBin && b - lastBin < 40)
            for (int i = lastBin + 1; i < b; i++) { curThr[i] = thr; curBrk[i] = brk; curHas[i] = true; }   // fill the small gaps
        curThr[b] = thr; curBrk[b] = brk; curHas[b] = true;
        if (b != lastBin) lastBin = b;
    }

    /// <summary>A few seconds after the line, iRacing shows the lap time: if that lap is the best, it becomes the ghost.</summary>
    void MaybePromote(Driver me, double t)
    {
        if (promoteUntil < 0 || t > promoteUntil) return;
        if (me.LastLapTime > 0 && me.BestLapTime > 0 && Math.Abs(me.LastLapTime - me.BestLapTime) < 0.002f)
        {
            Array.Copy(lastThr, bestThr, Bins); Array.Copy(lastBrk, bestBrk, Bins);
            for (int i = 1; i < Bins; i++) if (!lastHas[i]) { bestThr[i] = bestThr[i - 1]; bestBrk[i] = bestBrk[i - 1]; }
            haveBest = true; promoteUntil = -1;
            SaveGhost();
        }
    }

    void Identify(RaceSnapshot snap, int car, double t)
    {
        Driver? me = null, ahead = null, behind = null;
        foreach (var cls in snap.ClassLeaderboard)
        {
            for (int i = 0; i < cls.Drivers.Count; i++)
                if (cls.Drivers[i].CarIdx == car)
                {
                    me = cls.Drivers[i];
                    ahead = i > 0 ? cls.Drivers[i - 1] : null;
                    behind = i + 1 < cls.Drivers.Count ? cls.Drivers[i + 1] : null;
                    break;
                }
            if (me != null) break;
        }
        mapMe = me; mapAhead = ahead?.CarIdx ?? -1; mapBehind = behind?.CarIdx ?? -1; mapAheadD = ahead; mapBehindD = behind;
        if (me == null) { if (car != idCar) { idCar = car; nameTxt = numTxt = subTxt = aheadTxt = behindTxt = ""; } return; }
        if (me.ClassName != classLogoFor) { classLogoFor = me.ClassName; classLogo = LoadClassLogo(me.ClassName); }
        if (car != idCar || me.PositionInClass != idPos || me.CurrentLap != idLap)
        {
            idCar = car; idPos = me.PositionInClass; idLap = me.CurrentLap;
            nameTxt = NameFormat.Broadcast(me).ToUpperInvariant();
            numTxt = me.CarNumber ?? "";
            posTxt = me.PositionInClass > 0 ? "P" + Digits(me.PositionInClass) : "";
            classTxt = (me.ClassName ?? "").ToUpperInvariant();
            lapTxt = me.CurrentLap > 0 ? "LAP " + Digits(me.CurrentLap) : "";
        }
        if (t - gapsAt < 0.5) return;   // gaps refresh twice a second
        gapsAt = t;
        aheadTxt = me.IntervalSeconds is double ia && ia > 0 && ahead != null ? "-" + ia.ToString("0.0", CultureInfo.InvariantCulture) : "";
        aheadName = ahead != null ? NameFormat.Broadcast(ahead).ToUpperInvariant() : "";
        behindTxt = behind?.IntervalSeconds is double ib && ib > 0 ? "+" + ib.ToString("0.0", CultureInfo.InvariantCulture) : "";
        behindName = behind != null ? NameFormat.Broadcast(behind).ToUpperInvariant() : "";
    }

    readonly SKPaint logoPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.High };
    static readonly Dictionary<string, SKBitmap?> logos = new(StringComparer.OrdinalIgnoreCase);
    static SKBitmap? LoadClassLogo(string? cls)
    {
        var slug = ClassLogo.Resolve(cls);
        if (slug == null) return null;
        if (logos.TryGetValue(slug, out var b)) return b;
        try { var p = Assets.Resolve("logos", "classes", slug + ".png"); b = System.IO.File.Exists(p) ? Assets.Decode(p) : null; } catch { b = null; }
        logos[slug] = b;
        return b;
    }

    static readonly string[] small = Enumerable.Range(0, 400).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();
    static string Digits(int v) => v >= 0 && v < small.Length ? small[v] : v.ToString(CultureInfo.InvariantCulture);

    static SKColor Lerp(SKColor x, SKColor y, float u) => new(
        (byte)(x.Red + (y.Red - x.Red) * u), (byte)(x.Green + (y.Green - x.Green) * u), (byte)(x.Blue + (y.Blue - x.Blue) * u), 255);

    static SKColor Hex(string hex, byte alpha = 255)
    {
        var s = hex.TrimStart('#');
        if (s.Length != 6) return new SKColor(255, 255, 255, alpha);
        return new SKColor(Convert.ToByte(s[..2], 16), Convert.ToByte(s[2..4], 16), Convert.ToByte(s[4..6], 16), alpha);
    }
}
