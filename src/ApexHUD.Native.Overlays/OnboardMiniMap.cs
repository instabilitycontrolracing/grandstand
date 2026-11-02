using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// The onboard view's mini track map (2026-10-25): the circuit in a thin gold line, the car we're riding with as a
/// glowing dot, the class cars just ahead and behind in ivory, everyone else faint. For our own car the lap so far is
/// traced over the line - green where we're gaining on our best lap, red where we're losing it - and under the map
/// the sectors of this lap light up purple (class best), green (personal best) or amber.
/// </summary>
public sealed partial class OnboardFullRenderer
{
    Driver? mapMe; int mapAhead = -1, mapBehind = -1;
    string? mapKey; bool mapTried;
    SKPoint[]? mapRaw; float[]? mapCum;            // recorded line (map pixels) + cumulative length 0..1
    readonly SKPath mapPath = new();
    SKRect mapFor = SKRect.Empty;
    const int MapBins = 240;
    readonly SKPoint[] mapAt = new SKPoint[MapBins + 1];
    readonly float[] lapDelta = new float[MapBins]; readonly bool[] lapHas = new bool[MapBins];
    int lapBin = -1;
    SectorTimer.Mark[]? marks, prevMarks; double marksAt = -1; int marksCar = -1;
    static readonly SKColor Purple = new(0xB0, 0x7C, 0xFF), PbGreen = new(0x34, 0xD3, 0x99);
    float zoomK, zoomR = 1f; SKPoint zoomC; double mapLastMs = -1; string fightEye = "", fightPlace = "";

    /// <summary>The cars in the fight around the car we're riding with: it and any class car within 1.2 s ahead / behind.</summary>
    List<Driver> FightCars(RaceSnapshot snap)
    {
        var outL = new List<Driver>(3);
        if (mapMe == null || mapMe.IsInPit) return outL;
        Driver? a = null, b = null;
        foreach (var cl in snap.ClassLeaderboard)
            foreach (var d in cl.Drivers) { if (d.CarIdx == mapAhead) a = d; if (d.CarIdx == mapBehind) b = d; }
        outL.Add(mapMe);
        if (a != null && !a.IsInPit && mapMe.IntervalSeconds is double ia && ia > 0 && ia <= 1.2 && mapMe.LapDifference == 0) outL.Add(a);
        if (b != null && !b.IsInPit && b.IntervalSeconds is double ib && ib > 0 && ib <= 1.2 && b.LapDifference == 0) outL.Add(b);
        return outL;
    }

    void MiniMap(SKCanvas c, float cx, float cy, RaceSnapshot snap, OnboardData ob, float alpha, double nowMs)
    {
        var key = TrackMapCache.Resolve(snap.TrackConfigName, snap.TrackDisplayName, snap.TrackBroadcastName, snap.TrackRawName);
        if (key != mapKey || !mapTried) { mapKey = key; mapTried = true; LoadMap(key); mapFor = SKRect.Empty; }
        if (mapRaw == null || mapCum == null) return;
        var box = new SKRect(cx - 130f, cy - 78f, cx + 130f, cy + 70f);
        if (box != mapFor) Fit(box);
        byte A(float v) => (byte)Math.Clamp(v * 255f * alpha, 0, 255);

        // 2026-10-25 (Angelo: "the onboard should in a battle have the fighting into / down text ... keep it just as elegant
        // as it is now, but zoom into the part of the track where the battle is"): a class car within 1.2 s eases the map
        // into a zoom on the fight (soft-faded edges, no box) with the corner / straight above it; it eases back out after
        double mdt = mapLastMs < 0 ? 16 : Math.Clamp(nowMs - mapLastMs, 0, 100); mapLastMs = nowMs;
        var fight = FightCars(snap);
        // its own spot, left side of the screen (Angelo 16:45: keep the bottom-right map as it is, show the zoomed one elsewhere)
        float Hs = cy + 196f;
        var fb = box;   // Angelo 16:58: the bottom-right map itself zooms in during a fight
        zoomK += ((fight.Count >= 2 ? 1f : 0f) - zoomK) * (1f - MathF.Exp(-(float)mdt / 450f));
        string eye = "", place = "";
        if (fight.Count >= 2)
        {
            float refP = fight[0].LapDistPct;
            float Rel(float q) { float r = q - refP; if (r > 0.5f) r -= 1; if (r < -0.5f) r += 1; return r; }
            float back = fight.Min(d => Rel(d.LapDistPct)), front = fight.Max(d => Rel(d.LapDistPct));
            double lenM = TrackCorners.LengthM(snap.Extras.TrackLength); if (lenM <= 0) lenM = 5000;
            float frontPct = refP + front;
            var corner = TrackCorners.Upcoming(snap.TrackRawName, snap.Extras.TrackLength, frontPct, 700);
            float w0 = refP + back - (float)(120 / lenM), w1 = refP + front + (float)(350 / lenM);
            if (corner is { } cn) { float rel = cn.EndPct - frontPct; if (rel < -0.5f) rel += 1; if (rel > 0 && rel < 1200 / lenM) w1 = Math.Max(w1, frontPct + rel + (float)(60 / lenM)); }
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int k = 0; k <= 30; k++) { var q = PosAt(w0 + (w1 - w0) * k / 30f); x0 = Math.Min(x0, q.X); x1 = Math.Max(x1, q.X); y0 = Math.Min(y0, q.Y); y1 = Math.Max(y1, q.Y); }
            float R = Math.Min(fb.Width / Math.Max(1, x1 - x0), fb.Height / Math.Max(1, y1 - y0)) * 0.82f;
            zoomR = R; zoomC = new SKPoint((x0 + x1) / 2, (y0 + y1) / 2);
            float ahead2 = corner is { Inside: false } cc ? cc.StartPct - frontPct : 0; ahead2 -= MathF.Floor(ahead2);
            string? straight = corner == null || (corner is { Inside: false } && ahead2 > 350 / lenM) ? TrackCorners.Straight(snap.TrackRawName, snap.Extras.TrackLength, frontPct) : null;
            if (straight != null) { eye = "FIGHTING DOWN"; place = straight.ToUpperInvariant(); }
            else if (corner is { } h) { eye = h.Inside ? "FIGHTING THROUGH" : "FIGHTING INTO"; place = h.Name.ToUpperInvariant(); }
            fightEye = eye; fightPlace = place;
        }
        // 2026-10-25 (Angelo: "all the maps 3D, including the little ones"): the map lies on a tilted ground plane, on the
        // real terrain (TrackTerrain) over a soft dark bed so it reads over the onboard picture
        var terr = key == null ? null : TrackTerrain.For(key, Assets.Resolve("maps", key + ".json"), snap.TrackLatitude, snap.TrackLongitude, TrackCorners.LengthM(snap.Extras.TrackLength));
        var plane = new SKRect(box.Left, box.Top - box.Height * 0.3f, box.Right, box.Bottom + box.Height * 0.06f);
        float pin = box.Width * 0.16f;
        var persp = TrackTerrain.Perspective(plane, new SKPoint(box.Left + pin, box.Top - 2), new SKPoint(box.Right - pin, box.Top - 2),
                                             new SKPoint(box.Right + box.Width * 0.05f, box.Bottom + 4), new SKPoint(box.Left - box.Width * 0.05f, box.Bottom + 4));
        // the ground under the map: its own layer, softly faded round the map's area (not zoomed with the map)
        void Ground(SKMatrix zoomTx)
        {
            if (terr == null) return;
            var gb = new SKRect(box.Left - box.Width * 0.25f, box.Top - box.Height * 0.45f, box.Right + box.Width * 0.25f, box.Bottom + box.Height * 0.25f);
            int gl = c.SaveLayer(gb, null);
            c.ClipRect(gb, SKClipOperation.Intersect, true);                 // nothing outside the faded area (it's tilted)
            c.Save(); c.Concat(ref zoomTx);
            using (var bed = new SKPaint { IsAntialias = true, Color = new SKColor(0x08, 0x10, 0x22, A(0.55f * terr.FadeIn)) })
                c.DrawRect(new SKRect(box.Left - box.Width, box.Top - box.Height, box.Right + box.Width, box.Bottom + box.Height), bed);
            var gm = terr.ToMap.PostConcat(SKMatrix.CreateTranslation(-mapMx, -mapMy)).PostConcat(SKMatrix.CreateScale(mapSc, mapSc)).PostConcat(SKMatrix.CreateTranslation(mapCtr.X, mapCtr.Y));
            c.Concat(ref gm);
            using (var gp = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.Low, Color = SKColors.White.WithAlpha(A(0.95f * terr.FadeIn)) })
                c.DrawBitmap(terr.Ground, 0, 0, gp);
            c.Restore();
            using (var mk = new SKPaint { BlendMode = SKBlendMode.DstIn, Shader = SKShader.CreateRadialGradient(new SKPoint(box.MidX, box.MidY - box.Height * 0.08f), Math.Max(box.Width, box.Height) * 0.6f,
                       new[] { SKColors.White, SKColors.White.WithAlpha(200), SKColors.Transparent }, new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp) })
            {
                c.Save(); c.Scale(1f, box.Height / box.Width * 1.25f, box.MidX, box.MidY);   // an oval the shape of the map
                c.DrawRect(new SKRect(gb.Left, box.MidY - (box.MidY - gb.Top) * 3, gb.Right, box.MidY + (gb.Bottom - box.MidY) * 3), mk);
                c.Restore();
            }
            c.RestoreToCount(gl);
        }
        void Body(float inv)
        {
        // the circuit
        stroke.Shader = null; stroke.StrokeJoin = SKStrokeJoin.Round;
        stroke.StrokeWidth = 5.5f * inv; stroke.Color = new SKColor(0, 0, 0, A(0.45f)); c.DrawPath(mapPath, stroke);
        stroke.StrokeWidth = 2.2f * inv; stroke.Color = Gold.WithAlpha(A(0.75f)); c.DrawPath(mapPath, stroke);
        // start / finish tick
        var s0 = mapAt[0]; var s1 = mapAt[2];
        float dx = s1.X - s0.X, dy = s1.Y - s0.Y, len = MathF.Max(1e-3f, MathF.Sqrt(dx * dx + dy * dy));
        float nx = -dy / len * 7f * inv, ny = dx / len * 7f * inv;
        stroke.StrokeWidth = 2f * inv; stroke.Color = Ivory.WithAlpha(A(0.9f)); c.DrawLine(s0.X - nx, s0.Y - ny, s0.X + nx, s0.Y + ny, stroke);

        // our lap so far vs our best: green where the gap to the best lap shrinks, red where it grows
        if (ob.IsPlayerCar && ob.HasPedals && ob.LapPct >= 0 && lapBin >= 0)
        {
            int b = lapBin;                                                         // recorded by Feed every frame
            stroke.StrokeWidth = 3.4f * inv;
            for (int i = 1; i <= b; i++)
            {
                if (!lapHas[i] || !lapHas[i - 1]) continue;
                float rate = lapDelta[i] - lapDelta[i - 1];
                if (Math.Abs(rate) < 0.0008f) continue;                              // holding the same pace: leave the gold line
                float k = Math.Clamp(Math.Abs(rate) / 0.02f, 0.25f, 1f);
                var col = rate < 0 ? GreenHi : RedHi;
                stroke.Color = col.WithAlpha(A(0.9f * k));
                c.DrawLine(mapAt[i - 1], mapAt[i], stroke);
            }
        }

        // the cars: everyone faint, the class cars either side ivory, ours gold and glowing
        int me = ob.CamCarIdx;
        foreach (var cls in snap.ClassLeaderboard)
            foreach (var d in cls.Drivers)
            {
                if (d.CarIdx == me || d.CarIdx == mapAhead || d.CarIdx == mapBehind || d.LapDistPct < 0 || d.IsInPit) continue;
                fill.Shader = null; fill.Color = Ivory.WithAlpha(A(0.28f)); c.DrawCircle(PosAt(d.LapDistPct), (2.4f) * inv, fill);
            }
        foreach (var cls in snap.ClassLeaderboard)
            foreach (var d in cls.Drivers)
            {
                if ((d.CarIdx != mapAhead && d.CarIdx != mapBehind) || d.LapDistPct < 0) continue;
                var p = PosAt(d.LapDistPct);
                fill.Color = new SKColor(0, 0, 0, A(0.6f)); c.DrawCircle(p, (5.2f) * inv, fill);
                fill.Color = Ivory.WithAlpha(A(0.95f)); c.DrawCircle(p, (3.8f) * inv, fill);
            }
        if (mapMe != null && mapMe.LapDistPct >= 0)
        {
            var p = PosAt(ob.IsPlayerCar && ob.LapPct >= 0 ? ob.LapPct : mapMe.LapDistPct);
            float pulse = 0.5f + 0.5f * (float)Math.Sin(nowMs / 1000.0 * 4.0);
            fill.Color = Gold.WithAlpha(A(0.18f + 0.12f * pulse)); c.DrawCircle(p, (11f + 2f * pulse) * inv, fill);
            fill.Color = new SKColor(0, 0, 0, A(0.7f)); c.DrawCircle(p, (6.4f) * inv, fill);
            fill.Color = Gold.WithAlpha(A(1f)); c.DrawCircle(p, (5f) * inv, fill);
        }

        }
        c.Save(); c.Concat(ref persp);
        if (zoomK <= 0.01f) { Ground(SKMatrix.Identity); Body(1f); }   // the whole-track map, bottom right
        else
        {
            // in a fight it eases into a zoom on the stretch of track the fight is on, edges softly faded (no box)
            float Rk = 1f + (zoomR - 1f) * zoomK;
            var ctr = new SKPoint(box.MidX, box.MidY);
            var off = new SKPoint((ctr.X - zoomC.X) * zoomR * zoomK, (ctr.Y - zoomC.Y) * zoomR * zoomK);
            var fr = new SKRect(box.Left - 24, box.Top - 24, box.Right + 24, box.Bottom + 24);
            var zt = SKMatrix.CreateTranslation(-ctr.X, -ctr.Y).PostConcat(SKMatrix.CreateScale(Rk, Rk)).PostConcat(SKMatrix.CreateTranslation(ctr.X + off.X, ctr.Y + off.Y));
            Ground(zt);
            int layer = c.SaveLayer(fr, null);
            c.Save();
            c.Translate(ctr.X + off.X, ctr.Y + off.Y); c.Scale(Rk); c.Translate(-ctr.X, -ctr.Y);
            Body(1f / Rk);
            c.Restore();
            float e = Math.Clamp(zoomK * 1.4f, 0f, 1f);
            using var fx = SKShader.CreateLinearGradient(new SKPoint(fr.Left, 0), new SKPoint(fr.Right, 0),
                new[] { SKColors.White.WithAlpha((byte)(255 * (1 - e))), SKColors.White, SKColors.White, SKColors.White.WithAlpha((byte)(255 * (1 - e))) }, new[] { 0f, 0.16f, 0.84f, 1f }, SKShaderTileMode.Clamp);
            using var fy = SKShader.CreateLinearGradient(new SKPoint(0, fr.Top), new SKPoint(0, fr.Bottom),
                new[] { SKColors.White.WithAlpha((byte)(255 * (1 - e))), SKColors.White, SKColors.White, SKColors.White.WithAlpha((byte)(255 * (1 - e))) }, new[] { 0f, 0.16f, 0.84f, 1f }, SKShaderTileMode.Clamp);
            using var fxy = SKShader.CreateCompose(fx, fy, SKBlendMode.DstIn);
            using var fp = new SKPaint { Shader = fxy, BlendMode = SKBlendMode.DstIn };
            c.DrawRect(fr, fp);
            c.RestoreToCount(layer);
        }
        c.Restore();

        // the fight map (zoomed, left side) - Angelo 16:54: not there; the fight words go to the centred label instead
        if (false && zoomK > 0.01f)
        {
            var fr = new SKRect(fb.Left - 30, fb.Top - 30, fb.Right + 30, fb.Bottom + 30);
            using (var lp = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(255 * Math.Clamp(zoomK, 0f, 1f))) })
            {
                int layer = c.SaveLayer(fr, lp);
                c.Save();
                c.Translate(fb.MidX, fb.MidY); c.Scale(zoomR); c.Translate(-zoomC.X, -zoomC.Y);
                Body(1f / zoomR);
                c.Restore();
                using var fx = SKShader.CreateLinearGradient(new SKPoint(fr.Left, 0), new SKPoint(fr.Right, 0),
                    new[] { SKColors.Transparent, SKColors.White, SKColors.White, SKColors.Transparent }, new[] { 0f, 0.16f, 0.84f, 1f }, SKShaderTileMode.Clamp);
                using var fy = SKShader.CreateLinearGradient(new SKPoint(0, fr.Top), new SKPoint(0, fr.Bottom),
                    new[] { SKColors.Transparent, SKColors.White, SKColors.White, SKColors.Transparent }, new[] { 0f, 0.16f, 0.84f, 1f }, SKShaderTileMode.Clamp);
                using var fxy = SKShader.CreateCompose(fx, fy, SKBlendMode.DstIn);
                using var fp = new SKPaint { Shader = fxy, BlendMode = SKBlendMode.DstIn };
                c.DrawRect(fr, fp);
                c.RestoreToCount(layer);
            }
            if (fightPlace.Length > 0)
            {
                float ta = alpha * Math.Clamp((zoomK - 0.2f) / 0.6f, 0f, 1f);
                text.Typeface = Fonts.BarlowCondensed.Bold; text.TextSize = 16f; text.TextAlign = SKTextAlign.Left; text.Color = Gold.WithAlpha((byte)(255 * ta));   // #340: was 14
                TextTracked.DrawHalo(c, text, fightEye, fb.Left, fb.Top - 35f, 1.4f);
                text.Typeface = Fonts.BarlowCondensed.ExtraBold; text.TextSize = 26f; text.Color = Ivory.WithAlpha((byte)(255 * ta));
                float sp = 0.8f;
                while (text.MeasureText(fightPlace) + sp * fightPlace.Length > fb.Width + 20 && text.TextSize > 12) text.TextSize -= 0.5f;
                TextTracked.Draw(c, text, fightPlace, fb.Left, fb.Top - 9f, sp);
            }
        }

        // this lap's sectors
        int camCar = ob.CamCarIdx;
        if (marksCar != camCar || nowMs - marksAt > 400) { marks = SectorTimer.MarksFor(camCar); prevMarks = SectorTimer.PrevMarksFor(camCar); marksAt = nowMs; marksCar = camCar; }
        int n = marks?.Length ?? 0;
        if (n >= 2 && n <= 8)
        {
            var starts = SectorTimer.Starts();
            float pct = mapMe?.LapDistPct ?? -1;
            int curS = 0; for (int i = starts.Length - 1; i >= 0; i--) if (pct >= starts[i]) { curS = i; break; }
            float gap = 6f, w = (box.Width - gap * (n - 1)) / n, y = box.Bottom + 18f;
            text.Typeface = Fonts.Inter.Bold; text.TextSize = 13f; text.TextAlign = SKTextAlign.Center;   // #340: was 10 @0.9
            for (int i = 0; i < n; i++)
            {
                float x = box.Left + i * (w + gap);
                // 2026-10-25 (Angelo: chips went clear mid-lap): sectors done this lap in purple / green / yellow (a done
                // sector that wasn't timed counts as yellow); the one we're in and the ones to come show last lap's colour, faded
                var m = marks![i];
                float fa = 0.92f;
                if (i < curS) { if (m == SectorTimer.Mark.None) m = SectorTimer.Mark.Slower; }
                else
                {
                    // not done yet this lap: last lap's colour (still in Marks early in the lap, else in Prev), faded
                    if (m == SectorTimer.Mark.None && prevMarks != null && i < prevMarks.Length) m = prevMarks[i];
                    fa = m == SectorTimer.Mark.None ? 0.16f : 0.38f;
                }
                SKColor col = m switch { SectorTimer.Mark.ClassBest => Purple, SectorTimer.Mark.PersonalBest => PbGreen, SectorTimer.Mark.Slower => Amber, _ => new SKColor(255, 255, 255) };
                fill.Shader = null; fill.Color = col.WithAlpha(A(fa)); c.DrawRoundRect(new SKRect(x, y, x + w, y + 4f), 2f, 2f, fill);
                if (i == curS) { stroke.StrokeWidth = 1f; stroke.Color = Ivory.WithAlpha(A(0.7f)); c.DrawRoundRect(new SKRect(x - 1.5f, y - 1.5f, x + w + 1.5f, y + 5.5f), 3f, 3f, stroke); }
                text.Color = Tert.WithAlpha(A(1f));
                TextTracked.DrawHalo(c, text, SectorLabels[i], x + w / 2f, y + 19f, 1.2f);
            }
            text.TextAlign = SKTextAlign.Left;
        }
    }
    static readonly string[] SectorLabels = { "S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8" };

    void LoadMap(string? key)
    {
        mapRaw = null; mapCum = null;
        try
        {
            var json = key == null ? null : Assets.Resolve("maps", key + ".json");
            if (json == null || !System.IO.File.Exists(json)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(json));
            var list = new List<SKPoint>();
            foreach (var pt in doc.RootElement.GetProperty("points").EnumerateArray()) list.Add(new SKPoint(pt[0].GetSingle(), pt[1].GetSingle()));
            if (list.Count < 20) return;
            // lap distance along the line: the recorded points are in lap order from the start/finish line
            var cum = new float[list.Count + 1];
            for (int i = 1; i <= list.Count; i++)
            {
                var a = list[i - 1]; var b = list[i % list.Count];
                cum[i] = cum[i - 1] + SKPoint.Distance(a, b);
            }
            float tot = cum[^1]; if (tot <= 0) return;
            for (int i = 0; i < cum.Length; i++) cum[i] /= tot;
            mapRaw = list.ToArray(); mapCum = cum;
        }
        catch { mapRaw = null; mapCum = null; }
    }

    float mapSc, mapMx, mapMy; SKPoint mapCtr;
    void Fit(SKRect box)
    {
        mapFor = box;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in mapRaw!) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
        mapSc = Math.Min(box.Width / Math.Max(1, maxX - minX), box.Height / Math.Max(1, maxY - minY));
        mapMx = (minX + maxX) / 2; mapMy = (minY + maxY) / 2; mapCtr = new SKPoint(box.MidX, box.MidY);
        mapPath.Reset();
        for (int i = 0; i < mapRaw!.Length; i++) { var q = Tx(mapRaw[i]); if (i == 0) mapPath.MoveTo(q); else mapPath.LineTo(q); }
        mapPath.Close();
        for (int i = 0; i <= MapBins; i++) mapAt[i] = PosAt(i / (float)MapBins);
    }

    SKPoint Tx(SKPoint p) => new(mapCtr.X + (p.X - mapMx) * mapSc, mapCtr.Y + (p.Y - mapMy) * mapSc);

    /// <summary>Where on the drawn map a lap fraction is.</summary>
    SKPoint PosAt(float pct)
    {
        var cum = mapCum!; var raw = mapRaw!;
        pct -= MathF.Floor(pct);
        int lo = 0, hi = cum.Length - 1;
        while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (cum[mid] <= pct) lo = mid; else hi = mid; }
        float span = cum[hi] - cum[lo], k = span > 0 ? (pct - cum[lo]) / span : 0;
        var a = raw[lo % raw.Length]; var b = raw[hi % raw.Length];
        return Tx(new SKPoint(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k));
    }

    // ---- where we are (2026-10-25, Angelo: "if no battle is happening it should show the corner or straight somewhere,
    // elegantly and symmetrically"): centred at the top, between the driver plate and the gaps - a small gold word
    // (THROUGH / INTO / DOWN) over the place in ivory, a short gold hairline under it. Cross-fades as the place changes;
    // steps aside while the fight map (left) shows the same thing.
    string plEye = "", plName = "", plNextEye = "", plNextName = "";
    float plK; double plLastMs = -1;
    string plEyeFrom = ""; float plEyeK = 1f;      // #482: eyebrow-only cross-fade (INTO -> THROUGH on the same corner)

    /// <summary>2026-10-27 (#383, labels wrong on a replay): every label change is logged with where the car was.</summary>
    public static Action<string>? PlaceLog;
    /// <summary>#619: the bottom of the corner / straight name as drawn last frame (1080 units; 0 = no name showing).</summary>
    public static float PlaceBottom { get; private set; }

    void PlaceLabel(SKCanvas c, float W, RaceSnapshot snap, OnboardData ob, float alpha, double nowMs)
    {
        double dt = plLastMs < 0 ? 16 : Math.Clamp(nowMs - plLastMs, 0, 100); plLastMs = nowMs;
        float pct = ob.IsPlayerCar && ob.LapPct >= 0 ? ob.LapPct : mapMe?.LapDistPct ?? -1;
        string eye = "", name = "";
        if (zoomK > 0.5f && fightPlace.Length > 0) { eye = fightEye; name = fightPlace; }     // in a fight: FIGHTING INTO ...
        else if (pct >= 0)
        {
            double lenM = TrackCorners.LengthM(snap.Extras.TrackLength);
            var corner = TrackCorners.Upcoming(snap.TrackRawName, snap.Extras.TrackLength, pct, 350);
            if (corner is { } h) { eye = h.Inside ? "THROUGH" : "INTO"; name = h.Name.ToUpperInvariant(); }
            else if (TrackCorners.Straight(snap.TrackRawName, snap.Extras.TrackLength, pct) is { } st) { eye = "DOWN"; name = st.ToUpperInvariant(); }
        }
        // #482 (2026-10-28, Angelo: "if it is the same [place] then only the text should change"): the same place with a new
        // eyebrow (INTO -> THROUGH, or out of a fight) keeps the card and cross-fades just that word
        if (name == plName && eye != plEye && plName.Length > 0 && eye.Length > 0 && plK > 0.99f)
        {
            plEyeFrom = plEye; plEye = eye; plNextEye = eye; plEyeK = 0f;
            PlaceLog?.Invoke($"place: eyebrow {plEyeFrom} -> {plEye} {plName}");
        }
        plEyeK = Math.Min(1f, plEyeK + (float)(dt / 260.0));
        // cross-fade: the shown text fades out before the new one fades in
        if (name != plName || eye != plEye) { plNextEye = eye; plNextName = name; plK = Math.Max(0f, plK - (float)(dt / 220.0)); if (plK <= 0f) { plEye = plNextEye; plName = plNextName; if (plName.Length > 0) PlaceLog?.Invoke($"place: {plEye} {plName} - car {ob.CamCarIdx}{(ob.IsPlayerCar ? " (you)" : "")} at {pct:0.0000} of the lap, track {snap.TrackRawName} {snap.Extras.TrackLength}"); } }
        else plK = Math.Min(1f, plK + (float)(dt / 320.0));
        float a = alpha * plK;
        if (a <= 0.01f || plName.Length == 0) { PlaceBottom = 0f; return; }
        // 2026-10-25: themed to the corner (Dunlop bridge, PlayStation space, Michelin blue, maker colours) with a line of
        // its heritage - CornerTheme; plain gold for everything else
        CornerTheme.DrawLabel(c, W / 2f, 74f, plEye, plName, a, CornerTheme.HeritageOf(snap.TrackRawName, plName),
                              eyeFrom: plEyeK < 1f ? plEyeFrom : null, eyeMix: plEyeK);
        PlaceBottom = CornerTheme.LastLabelBottom;   // #619: the race-control bar sits under it on an onboard
        text.TextAlign = SKTextAlign.Left;
    }

    void DrawCentredTracked(SKCanvas c, string s, float cx, float y, float sp)
    {
        var align = text.TextAlign; text.TextAlign = SKTextAlign.Left;
        float w = text.MeasureText(s) + sp * Math.Max(0, s.Length - 1);
        TextTracked.Draw(c, text, s, cx - w / 2f, y, sp);
        text.TextAlign = align;
    }
}
