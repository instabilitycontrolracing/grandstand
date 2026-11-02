using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// V5 round 2 (2026-10-27, Angelo: "not as is" -> a BACK TO LIVE card in the language we already have). One design.
/// It is the REPLAY corner tag's sibling and the replay wipe's last beat: the LIVE wipe clears, and in the same top-right
/// corner where the REPLAY tag sat while we watched behind live, a BACK TO LIVE plate is cut in by the wipe's gold
/// leading edge (the slanted stripe with its thin echo, sweeping left to right). Same build as the REPLAY tag - deep
/// glass, an accent bar on the left (LIVE red instead of gold, with the wipe's red dot pulsing in it), Barlow Condensed
/// in ivory leaning with the wipe's slant - plus one tracked line of where the race is (lap, time to go). It holds a few
/// seconds and lifts away. Default OFF.
/// <para>Placement (round 3, 2026-10-27, Angelo: "can't ever block any other element and has to disappear after a few
/// seconds of being back at live"): only in FREE space per <see cref="VpLayout"/> - the session bar and watch, the ticker,
/// the tower, the battle card and map, the radio card, story / custom / featured cards and the pack's other cards are all
/// kept clear (and the centre third). First choice is the REPLAY tag's corner (24 px from the right, 30 px down), then the
/// first free spot down the right edge, then just right of the tower. No free spot = it is not shown. If something comes
/// up where it is, it moves to another free spot, or goes at once. It is gone 4 s after the picture is back at live (cut in at
/// +0.65 s as the wipe clears, held 2.9 s, out in 0.4 s).</para>
/// </summary>
public sealed class LiveTag : VpGraphic
{
    public const double InMs = 420, HoldMs = 2900, OutMs = 400;
    const float H = 62f, Slant = 0.2f;     // the wipe's lean (its band is h * 0.2 over its height)

    double shownAt = -1;
    VpLayer? body;
    float bodyW;
    float curTop = float.NaN; double lastDrawMs;
    readonly SKPaint flat = new() { IsAntialias = true };
    readonly SKPath stripe = new(), clip = new();

    public override bool Active(double nowMs) => shownAt >= 0 && nowMs - shownAt < HoldMs + OutMs;

    /// <summary>True when the last <see cref="Show"/> found no free spot (so nothing was shown).</summary>
    public bool Skipped { get; private set; }
    float curLeft = float.NaN;
    double movedAt = -1e12;

    /// <summary>The first free spot for the plate (top-left corner, design units), or NaN when there is none.</summary>
    SKPoint FindSpot(double nowMs)
    {
        float wdt = bodyW + 6f, ht = H + 26f;   // the plate, its drop shadow and the 22 px lift on the way out
        // 1. the REPLAY tag's corner, then down the right edge
        float right = 1920f - 24f;
        for (float y = 30f; y + ht <= 1064f; y += 8f)
            if (VpLayout.IsFree(new SKRect(right - wdt, y - 22f, right, y + ht - 22f), nowMs, VpLayout.SlotLive))
                return new SKPoint(right - bodyW, y);
        // 2. just right of the tower (the left third), top down
        float left = VpLayout.TowerVisible ? VpLayout.Tower.Right + 16f : 24f;
        for (float y = 30f; y + ht <= 1064f; y += 8f)
            if (VpLayout.IsFree(new SKRect(left, y - 22f, left + wdt, y + ht - 22f), nowMs, VpLayout.SlotLive))
                return new SKPoint(left, y);
        return new SKPoint(float.NaN, float.NaN);
    }

    public override void Hide(double nowMs) { if (Active(nowMs)) shownAt = Math.Min(shownAt, nowMs - HoldMs); }

    /// <summary>Back at live: <paramref name="subLine"/> = "LAP 14  ·  1:59:57 TO GO".</summary>
    public void Show(string subLine, double nowMs)
    {
        shownAt = nowMs;
        curTop = float.NaN; curLeft = float.NaN; Skipped = false; movedAt = -1e12;
        body?.Dispose();
        string sub = subLine.Replace("BACK TO LIVE  ·  ", "").ToUpperInvariant();
        const string Word = "BACK TO LIVE";
        float ww = VpText.Width(Word, Fonts.BarlowCondensed.Black, 36f, 1f);
        float sw = VpText.Width(sub, Fonts.Inter.SemiBold, 11f, 2.4f);
        bodyW = 6 + 46 + Math.Max(ww + 14, sw) + 34 + H * Slant;
        float s = H * Slant;
        body = VpLayer.Record(new SKRect(-20, -14, bodyW + 20, H + 24), c =>
        {
            using var shape = new SKPath();
            shape.MoveTo(0, 0); shape.LineTo(bodyW, 0); shape.LineTo(bodyW - s, H); shape.LineTo(0, H); shape.Close();
            using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 120), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10f) })
            { c.Save(); c.Translate(0, 5); c.DrawPath(shape, sh); c.Restore(); }
            // the wipe band's deep glass, darker at the edges
            using (var g = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(bodyW, 0),
                       new[] { new SKColor(9, 12, 18, 240), new SKColor(4, 6, 10, 246), new SKColor(9, 12, 18, 236) }, new[] { 0f, 0.6f, 1f }, SKShaderTileMode.Clamp) })
                c.DrawPath(shape, g);
            c.Save(); c.ClipPath(shape, SKClipOperation.Intersect, true);
            using (var red = new SKPaint { Color = Vp.LiveRed }) c.DrawRect(new SKRect(0, 0, 6, H), red);
            // the gold edge the wipe leaves behind: right side, leaning with it
            using (var gold = new SKPaint { IsAntialias = true, Color = Vp.Gold })
            {
                using var e = new SKPath();
                e.MoveTo(bodyW - 7, 0); e.LineTo(bodyW, 0); e.LineTo(bodyW - s, H); e.LineTo(bodyW - s - 7, H); e.Close();
                c.DrawPath(e, gold);
            }
            using (var top = new SKPaint { Color = Vp.Ivory.WithAlpha(24) }) c.DrawRect(new SKRect(0, 0, bodyW, 1), top);
            c.Restore();
            // the words lean with the wipe
            c.Save(); c.Translate(52, 0);
            c.Skew(-Slant, 0);
            VpText.Draw(c, Word, 0 + 30 * Slant, 37, Fonts.BarlowCondensed.Black, 36f, Vp.Ivory, 1f);
            c.Restore();
            VpText.Draw(c, sub, 52, 54, Fonts.Inter.SemiBold, 11f, Vp.Txt2, 2.4f);
        });
        // nowhere free: it is not shown at all
        if (float.IsNaN(FindSpot(nowMs).X)) { Skipped = true; shownAt = -1; }
    }

    public override void Draw(SKCanvas c, int w, int h, double nowMs)
    {
        Vp.LastScale = Vp.DeviceScale(c);
        if (!Active(nowMs) || body == null) { VpLayout.Release(VpLayout.SlotLive); return; }
        double t = nowMs - shownAt;
        float outK = Vp.EaseIn(Vp.Seg(t, HoldMs, HoldMs + OutMs));
        // stay where it is while that spot is free; otherwise the next free spot; none = it goes now
        bool here = !float.IsNaN(curTop) && VpLayout.IsFree(new SKRect(curLeft, curTop - 22f, curLeft + bodyW + 6f, curTop + H + 4f), nowMs, VpLayout.SlotLive);
        if (!here)
        {
            var spot = FindSpot(nowMs);
            if (float.IsNaN(spot.X))
            {
                // something came up where it is and there is nowhere else: it goes at once (never sits on a graphic)
                shownAt = -1; VpLayout.Release(VpLayout.SlotLive); return;
            }
            else
            {
                // it jumps (no glide through whatever took its place) and fades back in quickly there
                if (!float.IsNaN(curTop) && t >= 1) movedAt = nowMs;
                curTop = spot.Y; curLeft = spot.X;
            }
        }
        lastDrawMs = nowMs;
        float left = curLeft, top = curTop - outK * 22f;
        float vk = (1f - outK) * Vp.Seg(nowMs - movedAt, 0, 160);
        VpLayout.Claim(VpLayout.SlotLive, new SKRect(left, top, left + bodyW, top + H), nowMs);
        float s = H * Slant;

        int save = c.Save();
        c.Translate(left, top);
        // the gold leading edge cuts the plate in, left to right (the wipe's stripe + its thin echo)
        float lead = -s - 20f + (bodyW + s + 60f) * Vp.EaseIo(Vp.Seg(t, 0, InMs));
        clip.Reset();
        clip.MoveTo(-30, H + 20); clip.LineTo(-30 + s, -20); clip.LineTo(lead + s * 1.3f, -20); clip.LineTo(lead, H + 20); clip.Close();
        c.Save();
        c.ClipPath(clip, SKClipOperation.Intersect, true);
        body.Draw(c, 0, 0, vk);
        // the red dot pulsing in the live bar's space
        float pulse = 0.55f + 0.45f * MathF.Abs(MathF.Sin((float)(t / 1000.0 * 2.6)));
        flat.Color = Vp.LiveRed.WithA(vk * pulse * 0.45f);
        c.DrawCircle(28, H / 2f, 14f, flat);
        flat.Color = Vp.LiveRed.WithA(vk);
        c.DrawCircle(28, H / 2f, 8.5f, flat);
        c.Restore();
        if (t < InMs + 60)
        {
            float k = 1f - Vp.Seg(t, InMs - 80, InMs + 60);
            flat.Color = Vp.Gold.WithA(k);
            stripe.Reset(); stripe.MoveTo(lead - 12, H + 6); stripe.LineTo(lead - 12 + s * 1.3f, -6); stripe.LineTo(lead + s * 1.3f, -6); stripe.LineTo(lead, H + 6); stripe.Close();
            c.DrawPath(stripe, flat);
            flat.Color = Vp.GoldHi.WithA(0.66f * k);
            stripe.Reset(); stripe.MoveTo(lead + 10, H + 6); stripe.LineTo(lead + 10 + s * 1.3f, -6); stripe.LineTo(lead + 15 + s * 1.3f, -6); stripe.LineTo(lead + 15, H + 6); stripe.Close();
            c.DrawPath(stripe, flat);
        }
        c.RestoreToCount(save);
    }

    public override void Dispose()
    {
        body?.Dispose(); flat.Dispose(); stripe.Dispose(); clip.Dispose();
    }
}
