using System.IO;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// 2026-10-27 round 2 (V4, Angelo: "if it was sponsored by Oracle I want the outside border of the card to be the Oracle O
/// enlarged to size, the colour theme red, and it booms out from the logo"). A sponsored card's look worked out from the
/// partner's OWN logo file - nothing of the mark is drawn by Grandstand, it is measured:
/// <list type="bullet">
/// <item>The mark: the logo's biggest connected shape (so a wordmark next to an emblem is left out), from its alpha
/// channel - or, for a logo on a solid background, from its contrast with the corner colour.</item>
/// <item>Its outline: from the shape's centre, 360 rays find where the shape starts and ends - the outer contour and,
/// for a ring like an "O", the inner one (a solid mark gets a border 14 % of its radius thick). Kept as two closed paths
/// in a 0..1 box, so the card can stretch the mark to any size.</item>
/// <item>Its colour: the most common strong hue in the mark (gold if the logo is black / white / grey).</item>
/// </list>
/// Built once when the card is shown (event time, a few ms on a 256 px copy of the logo); never per frame.
/// </summary>
public sealed class SponsorTheme : IDisposable
{
    /// <summary>The logo as given (drawn small in the card's header and for the "boom").</summary>
    public SKImage Logo { get; }
    /// <summary>Outer / inner contour of the mark in a 0..1 box (the mark's own bounds).</summary>
    public SKPath Outer { get; } = new();
    public SKPath Inner { get; } = new();
    /// <summary>Outer minus inner (even-odd) - the mark's band, the card's border.</summary>
    public SKPath Band { get; } = new();
    /// <summary>The mark's bounds inside the logo picture, 0..1 of the logo.</summary>
    public SKRect MarkInLogo { get; private set; }
    /// <summary>Width / height of the mark.</summary>
    public float MarkAspect { get; private set; } = 1f;
    public SKColor Color { get; private set; } = Vp.GoldHi;
    /// <summary>The mark is a ring (it has a hole of its own) rather than a solid shape.</summary>
    public bool Ring { get; private set; }
    public string Source { get; private set; } = "";

    SponsorTheme(SKImage logo) { Logo = logo; }

    /// <summary>Loads and measures a logo file. Null when it can't be read or has no usable shape.</summary>
    public static SponsorTheme? FromFile(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            using var bmp = SKBitmap.Decode(path);
            if (bmp == null || bmp.Width < 8 || bmp.Height < 8) return null;
            var t = new SponsorTheme(SKImage.FromBitmap(bmp)) { Source = Path.GetFileName(path) };
            if (!t.Measure(bmp)) { t.Dispose(); return null; }
            return t;
        }
        catch { return null; }
    }

    bool Measure(SKBitmap src)
    {
        // a 256 px copy, unpremultiplied RGBA
        float sc = 384f / Math.Max(src.Width, src.Height);
        int w = Math.Max(8, (int)(src.Width * sc)), h = Math.Max(8, (int)(src.Height * sc));
        using var small = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var cv = new SKCanvas(small))
        using (var p = new SKPaint { FilterQuality = SKFilterQuality.High })
        { cv.Clear(SKColors.Transparent); cv.DrawBitmap(src, new SKRect(0, 0, w, h), p); }
        var px = small.Pixels;
        // the mask: alpha, or contrast against the corner colour when the picture has no transparency
        var mask = new bool[w * h];
        int opaque = 0;
        foreach (var c in px) if (c.Alpha > 200) opaque++;
        if (opaque > w * h * 0.97)
        {
            var bg = px[0];
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                int d = Math.Abs(c.Red - bg.Red) + Math.Abs(c.Green - bg.Green) + Math.Abs(c.Blue - bg.Blue);
                mask[i] = d > 90;
            }
        }
        else for (int i = 0; i < px.Length; i++) mask[i] = px[i].Alpha > 127;

        // the biggest connected shape (by its bounding box)
        var label = new int[w * h];
        int best = -1; float bestArea = 0; SKRectI bestBox = default; int lab = 0;
        var stack = new Stack<int>();
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || label[i] != 0) continue;
            lab++;
            int x0 = w, y0 = h, x1 = 0, y1 = 0;
            stack.Push(i); label[i] = lab;
            while (stack.Count > 0)
            {
                int k = stack.Pop(), x = k % w, y = k / w;
                if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
                if (x > 0 && mask[k - 1] && label[k - 1] == 0) { label[k - 1] = lab; stack.Push(k - 1); }
                if (x < w - 1 && mask[k + 1] && label[k + 1] == 0) { label[k + 1] = lab; stack.Push(k + 1); }
                if (y > 0 && mask[k - w] && label[k - w] == 0) { label[k - w] = lab; stack.Push(k - w); }
                if (y < h - 1 && mask[k + w] && label[k + w] == 0) { label[k + w] = lab; stack.Push(k + w); }
            }
            float area = (x1 - x0 + 1f) * (y1 - y0 + 1f);
            if (area > bestArea) { bestArea = area; best = lab; bestBox = new SKRectI(x0, y0, x1 + 1, y1 + 1); }
        }
        if (best < 0 || bestBox.Width < 6 || bestBox.Height < 6) return false;
        // the shape's centre and its main axis (a tilted mark is turned level, so it can frame a wide card; it untwists
        // as it grows out of the logo - see Tilt)
        double sx = 0, sy = 0, n0 = 0;
        for (int i = 0; i < label.Length; i++) if (label[i] == best) { sx += i % w; sy += i / w; n0++; }
        float cx = (float)(sx / n0) + 0.5f, cy = (float)(sy / n0) + 0.5f;
        double sxx = 0, syy = 0, sxy = 0;
        for (int i = 0; i < label.Length; i++) if (label[i] == best) { double dx = i % w + 0.5 - cx, dy = i / w + 0.5 - cy; sxx += dx * dx; syy += dy * dy; sxy += dx * dy; }
        double ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        double tr = sxx + syy, det = sxx * syy - sxy * sxy, disc = Math.Sqrt(Math.Max(0, tr * tr / 4 - det));
        double ratio = (tr / 2 + disc) / Math.Max(1e-6, tr / 2 - disc);
        if (ratio < 1.25 || Math.Abs(ang) < 4 * Math.PI / 180) ang = 0;   // round-ish or already level: leave it
        Tilt = (float)(ang * 180 / Math.PI);
        // a soft mask of the shape (bilinear between pixels) so the edges are found to a fraction of a pixel
        var mf = new float[w * h];
        for (int i = 0; i < mf.Length; i++) mf[i] = label[i] == best ? 1f : 0f;
        float M(float x, float y)
        {
            x -= 0.5f; y -= 0.5f;
            int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y); float fx = x - x0, fy = y - y0;
            float V(int xx, int yy) => xx < 0 || yy < 0 || xx >= w || yy >= h ? 0f : mf[yy * w + xx];
            return (V(x0, y0) * (1 - fx) + V(x0 + 1, y0) * fx) * (1 - fy) + (V(x0, y0 + 1) * (1 - fx) + V(x0 + 1, y0 + 1) * fx) * fy;
        }
        const int Rays = 360;
        var outer = new float[Rays]; var inner = new float[Rays];
        bool centreFilled = M(cx, cy) > 0.5f;
        int holeRays = 0;
        float maxR = MathF.Sqrt(w * w + h * h);
        for (int r = 0; r < Rays; r++)
        {
            float a = r * MathF.PI * 2 / Rays, dx = MathF.Cos(a), dy = MathF.Sin(a);
            float first = -1, last = -1, prevV = M(cx, cy), prevD = 0;
            for (float d = 0.25f; d < maxR; d += 0.25f)
            {
                float x = cx + dx * d, y = cy + dy * d;
                if (x < -1 || y < -1 || x > w + 1 || y > h + 1) break;
                float v = M(x, y);
                if ((prevV < 0.5f) != (v < 0.5f))
                {
                    float cross = prevD + (0.5f - prevV) / (v - prevV) * (d - prevD);   // where it passes 0.5
                    if (v >= 0.5f) { if (first < 0) first = cross; }
                    else last = cross;
                }
                prevV = v; prevD = d;
            }
            if (centreFilled && first < 0) first = 0;
            if (last < 0) { outer[r] = -1; continue; }
            outer[r] = last; inner[r] = Math.Max(0, first);
            if (first > 1.5f) holeRays++;
        }
        for (int r = 0; r < Rays; r++)
            if (outer[r] < 0)
            {
                int a = r, b = r;
                while (outer[(a + Rays) % Rays] < 0 && r - a < Rays) a--;
                while (outer[b % Rays] < 0 && b - r < Rays) b++;
                outer[r] = Math.Max(1, (outer[(a + Rays) % Rays] + outer[b % Rays]) / 2); inner[r] = (inner[(a + Rays) % Rays] + inner[b % Rays]) / 2;
            }
        Ring = !centreFilled && holeRays > Rays * 0.8f;
        Smooth(outer); Smooth(inner);
        for (int r = 0; r < Rays; r++)
            if (!Ring || inner[r] < outer[r] * 0.3f) inner[r] = outer[r] * 0.86f;          // solid mark: a border 14 % of its radius
            else inner[r] = Math.Min(inner[r], outer[r] - 1.2f);
        rayOuter = outer; rayInner = inner;
        // points turned level (by -Tilt) about the centre; their box is the mark's own 0..1 box
        float ca = (float)Math.Cos(-ang), sa = (float)Math.Sin(-ang);
        lvlOuter = new SKPoint[Rays]; lvlInner = new SKPoint[Rays];
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int r = 0; r < Rays; r++)
        {
            float a = r * MathF.PI * 2 / Rays, dx = MathF.Cos(a), dy = MathF.Sin(a);
            lvlOuter[r] = new SKPoint((dx * ca - dy * sa) * outer[r], (dx * sa + dy * ca) * outer[r]);
            lvlInner[r] = new SKPoint((dx * ca - dy * sa) * inner[r], (dx * sa + dy * ca) * inner[r]);
            minX = Math.Min(minX, lvlOuter[r].X); maxX = Math.Max(maxX, lvlOuter[r].X); minY = Math.Min(minY, lvlOuter[r].Y); maxY = Math.Max(maxY, lvlOuter[r].Y);
        }
        lvlBox = new SKRect(minX, minY, maxX, maxY);
        MarkAspect = lvlBox.Width / Math.Max(1e-3f, lvlBox.Height);
        // where the (level) mark sits in the logo picture: its centre and size, 0..1 of the picture
        MarkCentre = new SKPoint(cx / w, cy / h);
        MarkSize = new SKSize(lvlBox.Width / w, lvlBox.Height / h);
        MarkInLogo = new SKRect(bestBox.Left / (float)w, bestBox.Top / (float)h, bestBox.Right / (float)w, bestBox.Bottom / (float)h);
        BuildPaths(1f);

        // the colour: the most common strong hue in the mark
        var bins = new float[36];
        for (int i = 0; i < px.Length; i++)
        {
            if (label[i] != best) continue;
            var c = px[i];
            c.ToHsv(out float hh, out float ss, out float vv);
            if (ss < 35 || vv < 25) continue;
            int bi = (int)(hh / 10) % 36;
            bins[bi] += ss / 100f;
        }
        int top = 0; for (int i = 1; i < 36; i++) if (bins[i] > bins[top]) top = i;
        if (bins[top] > 4)
        {
            // the mean colour of the mark's pixels in the winning hue band (and its neighbours)
            float r0 = 0, g0 = 0, b0 = 0; int k2 = 0;
            for (int i = 0; i < px.Length; i++)
            {
                if (label[i] != best) continue;
                var c = px[i]; c.ToHsv(out float hh, out float ss, out float vv);
                if (ss < 35 || vv < 25 || Math.Abs(((int)(hh / 10) - top + 54) % 36 - 18) > 1) continue;
                r0 += c.Red; g0 += c.Green; b0 += c.Blue; k2++;
            }
            if (k2 > 0) Color = new SKColor((byte)(r0 / k2), (byte)(g0 / k2), (byte)(b0 / k2));
        }
        return true;
    }

    static void Smooth(float[] v)
    {
        int n = v.Length; var o = (float[])v.Clone();
        for (int i = 0; i < n; i++)
        {
            float s = 0, wsum = 0;
            for (int k = -4; k <= 4; k++) { float wk = 5 - Math.Abs(k); s += o[(i + k + n) % n] * wk; wsum += wk; }
            v[i] = s / wsum;
        }
    }

    float[] rayOuter = Array.Empty<float>(), rayInner = Array.Empty<float>();
    SKPoint[] lvlOuter = Array.Empty<SKPoint>(), lvlInner = Array.Empty<SKPoint>();
    SKRect lvlBox;

    /// <summary>The mark's main axis in the logo picture (degrees; 0 = level). The card's border starts at this tilt and
    /// untwists as it grows.</summary>
    public float Tilt { get; private set; }
    /// <summary>Centre of the mark in the logo picture (0..1) and the size of its level box (0..1 of the picture).</summary>
    public SKPoint MarkCentre { get; private set; }
    public SKSize MarkSize { get; private set; }
    /// <summary>How much of the band's own thickness is kept (1 = the mark exactly; less = thinner, same outline).</summary>
    public float BandKeep { get; private set; } = 1f;

    /// <summary>Rebuilds the 0..1 outline paths, keeping <paramref name="keep"/> of the band's thickness.</summary>
    public void BuildPaths(float keep)
    {
        BandKeep = keep;
        Outer.Reset(); Inner.Reset();
        float bw = Math.Max(1e-3f, lvlBox.Width), bh = Math.Max(1e-3f, lvlBox.Height);
        SKPoint N(SKPoint p) => new((p.X - lvlBox.Left) / bw, (p.Y - lvlBox.Top) / bh);
        for (int r = 0; r < lvlOuter.Length; r++)
        {
            var o = lvlOuter[r]; var i = lvlInner[r];
            var ik = new SKPoint(o.X + (i.X - o.X) * keep, o.Y + (i.Y - o.Y) * keep);
            if (r == 0) { Outer.MoveTo(N(o)); Inner.MoveTo(N(ik)); } else { Outer.LineTo(N(o)); Inner.LineTo(N(ik)); }
        }
        Outer.Close(); Inner.Close();
        Band.Reset(); Band.AddPath(Outer); Band.AddPath(Inner); Band.FillType = SKPathFillType.EvenOdd;
    }

    /// <summary>The band's average thickness as a fraction of the mark's level height (before thinning).</summary>
    public float BandThickness
    {
        get
        {
            if (lvlOuter.Length == 0) return 0.1f;
            float s = 0;
            for (int r = 0; r < lvlOuter.Length; r++) s += SKPoint.Distance(lvlOuter[r], lvlInner[r]);
            return s / lvlOuter.Length / Math.Max(1e-3f, lvlBox.Height);
        }
    }

    /// <summary>The colour pushed to read as an accent on dark glass (never too dark).</summary>
    public SKColor Accent
    {
        get
        {
            Color.ToHsv(out float h, out float s, out float v);
            return SKColor.FromHsv(h, Math.Min(s, 92), Math.Max(v, 78));
        }
    }

    /// <summary>The matrix that puts the 0..1 mark box onto <paramref name="dst"/>.</summary>
    public static SKMatrix BoxTo(SKRect dst) => SKMatrix.CreateScaleTranslation(dst.Width, dst.Height, dst.Left, dst.Top);

    /// <summary>The smallest frame (centred on the content) whose INNER outline holds the whole content rectangle, as a
    /// multiple of the content size, with a margin (fraction of the content size) kept inside.</summary>
    public float FitFactor(float contentAspect, float margin = 0.03f)
    {
        float lo = 1f, hi = 3f;
        for (int it = 0; it < 24; it++)
        {
            float s = (lo + hi) / 2;
            // content corners in the 0..1 frame box
            float hw = (1f + margin * 2) / s / 2f, hh = (1f + margin * 2) / s / 2f;
            bool ok = true;
            foreach (var (x, y) in new[] { (0.5f - hw, 0.5f - hh), (0.5f + hw, 0.5f - hh), (0.5f + hw, 0.5f + hh), (0.5f - hw, 0.5f + hh),
                                            (0.5f, 0.5f - hh), (0.5f, 0.5f + hh), (0.5f - hw, 0.5f), (0.5f + hw, 0.5f) })
                if (!Inner.Contains(x, y)) { ok = false; break; }
            if (ok) hi = s; else lo = s;
        }
        return hi;
    }

    public void Dispose() { Logo.Dispose(); Outer.Dispose(); Inner.Dispose(); Band.Dispose(); }
}
