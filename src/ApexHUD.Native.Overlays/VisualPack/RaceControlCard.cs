using SkiaSharp;

namespace ApexHUD.Native.Overlays;

public enum RcCategory { TrackLimits, Warning, Penalty, Repair, Disqualified, Investigation, Noted, Cleared }

/// <summary>One race-control message (what iRacing told us - nothing invented).</summary>
/// <param name="Stamp">left column, e.g. "L12" (lap) </param>
/// <param name="Clock">sim clock "14:32" or "" </param>
/// <param name="Text">the message, e.g. "TRACK LIMITS AT TERTRE ROUGE"</param>
/// <param name="Incidents">the car's incident points now (-1 unknown)</param>
/// <param name="IncidentLimit">the session's limit (0 = none / unknown)</param>
public sealed record RcMessage(RcCategory Category, string Stamp, string Clock, string CarNumber, string Driver, string ClassName,
                               string ClassColor, string Text, int Incidents = -1, int IncidentLimit = 0);

/// <summary>
/// W12 (2026-10-27): penalty / track-limits card - iRacing's race-control decisions in the style of the F1 race-control
/// messages, in OUR race-control radio card's theme (deep glass, blue bloom from the top, blue hairlines top and bottom,
/// the slow light sweep, RACE CONTROL with the little voice meter). Bottom-right corner.
/// <para>Base: one message - stamp column (lap / clock), a coloured decision tag (TRACK LIMITS amber, PENALTY red,
/// WARNING amber outline, REPAIR orange, DISQUALIFIED red, NOTED ivory), the car chip and driver, the message in capitals,
/// and the car's incident points against the limit. Elevated ("message log"): the last three messages stack like the
/// race-control screen - the new one slides in on top and pushes the others down and dims them, its decision tag lands
/// with a stamp (scale-in, flash), and the incident points are a segmented bar that fills to the new total.</para>
/// </summary>
public sealed class RaceControlCard : VpGraphic
{
    public const float W = 400f, RightMargin = 40f, BottomMargin = 40f;
    /// <summary>Drawn at the radio card's 90 % (2026-10-27).</summary>
    public float Scale { get; set; } = 0.9f;
    float curBottom = float.NaN, curScale = float.NaN; double lastDrawMs;
    public const double HoldMs = 8500, OutMs = 380;
    const int Keep = 3;

    readonly List<(RcMessage Msg, VpLayer? Row, float H)> log = new();
    double shownAt = -1;
    VpLayer? frame;
    float frameH;
    bool bakedElevated;

    readonly SKPaint flat = new() { IsAntialias = true };
    readonly SKPaint sweep = new() { IsAntialias = false };
    readonly SKPaint flash = new() { IsAntialias = true, BlendMode = SKBlendMode.Plus };
    readonly SKPath clip = new();

    public RaceControlCard()
    {
        sweep.Shader = SKShader.CreateLinearGradient(new SKPoint(0, -60), new SKPoint(0, 60),
            new[] { SKColors.White.WithAlpha(0), SKColors.White.WithAlpha(18), SKColors.White.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
    }

    public override bool Active(double nowMs) => shownAt >= 0 && nowMs - shownAt < HoldMs + OutMs;

    public override void Hide(double nowMs) { if (Active(nowMs)) shownAt = Math.Min(shownAt, nowMs - HoldMs); }

    /// <summary>A new message (the elevated design keeps the last three).</summary>
    public void Show(RcMessage m, double nowMs)
    {
        bool wasUp = Active(nowMs);
        if (!wasUp) { foreach (var e in log) e.Row?.Dispose(); log.Clear(); }
        log.Insert(0, (m, null, 0));
        while (log.Count > Keep) { log[^1].Row?.Dispose(); log.RemoveAt(log.Count - 1); }
        shownAt = nowMs;
        pushAt = wasUp ? nowMs : -1;
        Bake();
    }

    double pushAt = -1;

    static SKColor TagColor(RcCategory c) => c switch
    {
        RcCategory.TrackLimits or RcCategory.Warning => Vp.Amber,
        RcCategory.Penalty or RcCategory.Disqualified => Vp.LiveRed,
        RcCategory.Repair => new SKColor(0xFF, 0x7A, 0x1A),
        RcCategory.Investigation => new SKColor(0x6E, 0x9B, 0xFF),
        RcCategory.Cleared => Vp.Gain,
        _ => Vp.Ivory,
    };

    static string TagText(RcCategory c) => c switch
    {
        RcCategory.TrackLimits => "TRACK LIMITS",
        RcCategory.Warning => "WARNING",
        RcCategory.Penalty => "PENALTY",
        RcCategory.Repair => "REPAIR",
        RcCategory.Disqualified => "DISQUALIFIED",
        RcCategory.Investigation => "UNDER INVESTIGATION",
        RcCategory.Cleared => "NO FURTHER ACTION",
        _ => "NOTED",
    };

    static List<string> Wrap(string text, SKTypeface tf, float size, float track, float max, int rows)
    {
        var outL = new List<string>();
        string line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string test = line.Length == 0 ? word : line + " " + word;
            if (VpText.Width(test, tf, size, track) <= max) { line = test; continue; }
            if (line.Length > 0) outL.Add(line);
            line = word;
            if (outL.Count == rows) return outL;
        }
        if (line.Length > 0 && outL.Count < rows) outL.Add(line);
        return outL;
    }

    void Bake()
    {
        bakedElevated = Elevated;
        // rows (only the new / missing ones)
        for (int i = 0; i < log.Count; i++)
        {
            if (log[i].Row != null) continue;
            var m = log[i].Msg;
            var lines = Wrap(m.Text.ToUpperInvariant(), Fonts.Inter.SemiBold, 16f, 0.4f, W - 44 - 54, 3);
            float rh = 74f + lines.Count * 22f;
            var row = VpLayer.Record(new SKRect(0, 0, W, rh), c => Row(c, m, lines, rh));
            log[i] = (m, row, rh);
        }
        var head = log[0];
        stampW = VpText.Width(TagText(head.Msg.Category), Fonts.Inter.Bold, 10.5f, 1.8f) + 18f;
        incLabel = head.Msg.IncidentLimit > 0 ? $"{head.Msg.Incidents}x / {head.Msg.IncidentLimit}x" : $"{head.Msg.Incidents}x";
        frameH = Elevated ? 52f + RowsHeight() + 44f : 52f + head.H + (head.Msg.Incidents >= 0 ? 44f : 10f);
        frame?.Dispose();
        frame = VpLayer.Record(new SKRect(-30, -frameH - 30, W + 30, 30), c => Frame(c, frameH));
    }

    float RowsHeight()
    {
        float hgt = 0;
        for (int i = 0; i < log.Count; i++) hgt += log[i].H + (i > 0 ? 6f : 0f);
        return hgt;
    }

    static void Frame(SKCanvas c, float fh)
    {
        var card = new SKRect(0, -fh, W, 0);
        using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 120), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14) })
            c.DrawRect(new SKRect(card.Left, card.Top + 6, card.Right, card.Bottom + 6), sh);
        using (var g = new SKPaint { Color = new SKColor(8, 10, 16, 242) }) c.DrawRect(card, g);
        using (var bloom = new SKPaint { Shader = SKShader.CreateRadialGradient(new SKPoint(card.MidX, card.Top + 20), W * 0.8f,
                   new[] { Vp.RcBlue.WithAlpha(110), Vp.RcBlue.WithAlpha(30), Vp.RcBlue.WithAlpha(0) }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp) })
            c.DrawRect(card, bloom);
        using (var hl = new SKPaint { Color = Vp.RcBlue.WithAlpha(210) })
        {
            c.DrawRect(new SKRect(card.Left, card.Top, card.Right, card.Top + 2), hl);
            c.DrawRect(new SKRect(card.Left, card.Bottom - 2, card.Right, card.Bottom), hl);
        }
        VpText.Draw(c, "RACE CONTROL", 22, card.Top + 33, Fonts.Inter.Bold, 12.5f, Vp.Ivory, 2.8f);
        VpText.Draw(c, "STEWARDS' DECISION", W - 64, card.Top + 33, Fonts.Inter.SemiBold, 9.5f, Vp.Txt3, 2f, 1);
        using (var hr = new SKPaint { Color = Vp.Ivory.WithAlpha(22) }) c.DrawRect(new SKRect(22, card.Top + 46, W - 22, card.Top + 47), hr);
    }

    static void Row(SKCanvas c, RcMessage m, List<string> lines, float rh)
    {
        // stamp column
        VpText.Draw(c, m.Stamp, 22, 26, Fonts.BarlowCondensed.Bold, 20f, Vp.Ivory, 0.5f);
        if (m.Clock.Length > 0) VpText.Draw(c, m.Clock, 22, 44, Fonts.Inter.SemiBold, 10f, Vp.Txt3, 1.2f);
        float x = 76;
        // tag pill
        var tc = TagColor(m.Category);
        string tag = TagText(m.Category);
        float tw = VpText.Width(tag, Fonts.Inter.Bold, 10.5f, 1.8f) + 18f;
        var tr = new SKRect(x, 10, x + tw, 30);
        if (m.Category == RcCategory.Warning || m.Category == RcCategory.Noted || m.Category == RcCategory.Investigation)
        {
            using var o = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = tc };
            c.DrawRoundRect(tr, 3, 3, o);
            VpText.Draw(c, tag, tr.MidX, 24, Fonts.Inter.Bold, 10.5f, tc, 1.8f, 0);
        }
        else
        {
            using (var f = new SKPaint { IsAntialias = true, Color = tc }) c.DrawRoundRect(tr, 3, 3, f);
            bool dark = tc.Red + tc.Green + tc.Blue > 450;
            VpText.Draw(c, tag, tr.MidX, 24, Fonts.Inter.Bold, 10.5f, dark ? new SKColor(10, 12, 16) : SKColors.White, 1.8f, 0);
        }
        // car chip + driver
        float cy = 52;
        float cw = Math.Max(32f, VpText.Width(m.CarNumber, Fonts.Inter.Bold, 12f) + 14f);
        using (var cb = new SKPaint { IsAntialias = true, Color = SKColors.White }) c.DrawRoundRect(new SKRect(x, cy - 10, x + cw, cy + 9), 2, 2, cb);
        VpText.Draw(c, m.CarNumber, x + cw / 2, cy + 4.5f, Fonts.Inter.Bold, 12f, new SKColor(8, 10, 16), 0, 0);
        using (var cl = new SKPaint { Color = Vp.ClassLine(m.ClassColor) }) c.DrawRect(new SKRect(x + cw + 8, cy - 9, x + cw + 11, cy + 8), cl);
        string drv = m.Driver.ToUpperInvariant();
        float ds = VpText.Fit(drv, Fonts.Inter.Bold, 13f, W - 22 - (x + cw + 20), 1f, 9f);
        VpText.Draw(c, drv, x + cw + 20, cy + 5, Fonts.Inter.Bold, ds, Vp.Ivory, 1f);
        // the message
        float y = 86;
        foreach (var ln in lines) { VpText.Draw(c, ln, x, y, Fonts.Inter.SemiBold, 16f, Vp.Txt2, 0.4f); y += 22; }
    }

    public override void Draw(SKCanvas c, int w, int h, double nowMs)
    {
        Vp.LastScale = Vp.DeviceScale(c);
        if (!Active(nowMs) || frame == null || log.Count == 0) return;
        if (bakedElevated != Elevated) Bake();
        double t = nowMs - shownAt;
        float inK = Vp.EaseOutExpo(Vp.Seg(t, 0, 360));
        float outK = Vp.EaseIn(Vp.Seg(t, HoldMs, HoldMs + OutMs));
        float a = (pushAt >= 0 ? 1f : inK) * (1f - outK);
        float slide = (pushAt >= 0 ? 0f : (1f - inK) * 30f) + outK * 30f;
        int save = c.Save();
        // 2026-10-27 round 2 (Angelo: "it's too low"): it lives in the RADIO column - the team-radio card's right edge,
        // same 90 % size, standing on the radio card's anchor; when a radio card is up it stacks right above it. Never
        // into the session bar / ticker or the battle map above it. Moves smoothly when the radio card comes or goes.
        float scale = Scale;
        float right = VpLayout.RadioRight, targetBottom = VpLayout.RadioVisible ? VpLayout.Radio.Top - 12f : VpLayout.RadioBottom;
        float limit = VpLayout.CardTop;   // #413
        if (VpLayout.BattleMapVisible && VpLayout.BattleMap.Left < right && VpLayout.BattleMap.Right > right - W * scale) limit = Math.Max(limit, VpLayout.BattleMap.Bottom + 12f);
        float cardH = frameH * scale;
        if (targetBottom - cardH < limit)
        {
            // not enough room above the radio card (the elevated log is tall): shrink to fit, down to 65 %, before overlapping
            scale = Math.Max(0.65f, (targetBottom - limit) / Math.Max(1f, frameH));
            cardH = frameH * scale;
            if (targetBottom - cardH < limit) targetBottom = limit + cardH;
        }
        if (float.IsNaN(curScale) || t < 1) curScale = scale;
        else curScale += (scale - curScale) * Math.Min(1f, (float)Math.Max(0, nowMs - lastDrawMs) / 140f);
        scale = curScale;
        if (float.IsNaN(curBottom) || t < 1) curBottom = targetBottom;
        else curBottom += (targetBottom - curBottom) * Math.Min(1f, (float)Math.Max(0, nowMs - lastDrawMs) / 140f);
        if (curBottom - frameH * scale < limit) curBottom = limit + frameH * scale;   // #413: never over the stack while it eases
        lastDrawMs = nowMs;
        float bottom = curBottom;
        VpLayout.Claim(VpLayout.SlotRaceControl, new SKRect(right - W * scale, bottom - frameH * scale, right, bottom), nowMs);   // round 3
        c.Translate(right - W * scale + slide, bottom);
        c.Scale(scale);
        frame.Draw(c, 0, 0, a);
        float top = -frameH;
        // light sweep down the card
        if (!Perf.Light)
        {
            c.Save();
            clip.Reset(); clip.AddRect(new SKRect(0, top, W, 0));
            c.ClipPath(clip);
            float sy = top + (float)((nowMs % 4500) / 4500.0) * frameH;
            c.Translate(0, sy);
            sweep.Color = SKColors.White.WithA(a);
            c.DrawRect(0, -60, W, 120, sweep);
            c.Restore();
        }
        // voice meter (race control is "talking" while the card arrives)
        for (int i = 0; i < 5; i++)
        {
            double ph = t / 1000.0 * (6.0 + i * 1.7) + i * 1.3;
            float k = t < 2500 ? (float)(0.35 + 0.65 * Math.Abs(Math.Sin(ph))) : 0.25f;
            float bh = 3f + 9f * k;
            flat.Color = Vp.Ivory.WithA(0.9f * a);
            c.DrawRoundRect(new SKRect(W - 46 + i * 5, top + 33 - bh, W - 43 + i * 5, top + 33), 1, 1, flat);
        }
        float y = top + 52f;
        if (!Elevated)
        {
            var (m, row, rh) = log[0];
            row?.Draw(c, 0, y, a * Vp.Seg(t, 80, 300));
            y += rh;
            if (m.Incidents >= 0) DrawIncidents(c, m, y + 10, t, a, false);
        }
        else
        {
            // the log: new row slides in at the top, older rows pushed down and dimmed
            float push = pushAt >= 0 ? Vp.EaseOutExpo(Vp.Seg(nowMs, pushAt, pushAt + 520)) : 1f;
            float firstH = log[0].H + 6f;
            float yy = y;
            for (int i = 0; i < log.Count; i++)
            {
                var (m, row, rh) = log[i];
                float dim = i == 0 ? 1f : 0.42f;
                float rowA = a * dim;
                float off = i == 0 ? 0f : -(1f - push) * firstH;
                if (i == 0)
                {
                    float rk = Vp.EaseOutExpo(Vp.Seg(t, 60, 420));
                    c.Save();
                    clip.Reset(); clip.AddRect(new SKRect(0, yy, W, yy + rh));
                    c.ClipPath(clip);
                    row?.Draw(c, (1f - rk) * 40f, yy, a * rk);
                    StampFlash(c, m, yy, t, a);
                    c.Restore();
                }
                else row?.Draw(c, 0, yy + off, rowA);
                if (i == 0 && log.Count > 1)
                {
                    flat.Color = Vp.Ivory.WithA(0.08f * a);
                    c.DrawRect(new SKRect(22, yy + rh + 2, W - 22, yy + rh + 3), flat);
                }
                yy += rh + 6f;
            }
            DrawIncidents(c, log[0].Msg, -34, t, a, true);
        }
        c.RestoreToCount(save);
    }

    void StampFlash(SKCanvas c, RcMessage m, float rowTop, double t, float a)
    {
        // the decision tag lands like a stamp: a bright frame that shrinks onto it and a flash
        float k = Vp.Seg(t, 360, 620);
        if (k <= 0f || k >= 1f) return;
        var tr = new SKRect(76, rowTop + 10, 76 + stampW, rowTop + 30);
        float grow = (1f - Vp.EaseOut(k)) * 10f;
        flash.Color = TagColor(m.Category).WithA(a * (1f - k) * 0.8f);
        c.DrawRoundRect(new SKRect(tr.Left - grow, tr.Top - grow, tr.Right + grow, tr.Bottom + grow), 4, 4, flash);
    }

    void DrawIncidents(SKCanvas c, RcMessage m, float y, double t, float a, bool segmented)
    {
        if (m.Incidents < 0) return;
        smallP ??= new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 10f };
        valP ??= new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13f };
        smallP.Color = Vp.Txt3.WithA(a);
        TextTracked.Draw(c, smallP, "INCIDENT POINTS", 22, y + 4, 2f);
        valP.Color = Vp.Ivory.WithA(a);
        valP.TextAlign = SKTextAlign.Right;
        TextBlobs.Draw(c, incLabel, W - 22, y + 5, valP);
        valP.TextAlign = SKTextAlign.Left;
        if (m.IncidentLimit <= 0) return;
        float bx = 22, bw = W - 44, by = y + 14;
        float k = Vp.EaseIo(Vp.Seg(t, 500, 1300));
        float frac = Math.Clamp(m.Incidents / (float)m.IncidentLimit, 0, 1);
        if (!segmented)
        {
            flat.Color = Vp.Ivory.WithA(0.12f * a);
            c.DrawRect(new SKRect(bx, by, bx + bw, by + 4), flat);
            flat.Color = (frac > 0.75f ? Vp.LiveRed : frac > 0.5f ? Vp.Amber : Vp.Ivory).WithA(a);
            c.DrawRect(new SKRect(bx, by, bx + bw * frac * k, by + 4), flat);
            return;
        }
        int n = Math.Min(m.IncidentLimit, 40);
        float sw = (bw - (n - 1) * 2f) / n;
        float filled = m.Incidents * k;
        for (int i = 0; i < n; i++)
        {
            float f = Vp.Clamp01(filled - i);
            float x = bx + i * (sw + 2f);
            flat.Color = Vp.Ivory.WithA(0.12f * a);
            c.DrawRect(new SKRect(x, by, x + sw, by + 6), flat);
            if (f > 0f)
            {
                float u = i / (float)Math.Max(1, n - 1);
                flat.Color = (u > 0.75f ? Vp.LiveRed : u > 0.5f ? Vp.Amber : Vp.Ivory).WithA(a);
                c.DrawRect(new SKRect(x, by, x + sw * f, by + 6), flat);
            }
        }
    }

    SKPaint? smallP, valP;
    string incLabel = "";
    float stampW;

    public override void Dispose()
    {
        foreach (var e in log) e.Row?.Dispose();
        frame?.Dispose();
        flat.Dispose(); sweep.Dispose(); flash.Dispose(); clip.Dispose(); smallP?.Dispose(); valP?.Dispose();
    }
}
