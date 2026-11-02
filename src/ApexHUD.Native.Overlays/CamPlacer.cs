using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// #611 (2026-10-30, Angelo: "the video box for face and pedal cam were being covered many many times by parts of the
/// overlay; we need to figure that one out to make it content aware without it weighing too much"): the decisions of the
/// light cam placer - pure maths, no OBS, so the preview tools run the very same code. The OBS side
/// (GpuHost/ObsCams.cs) feeds it the graphics on screen (canvas pixels) every ~150 ms and carries out what it says.
/// <list type="bullet">
/// <item>Each cam has a home slot (where it sits in the OBS scene) and a few same-size alternatives, nearest first.</item>
/// <item>When the graphics cover more than <see cref="Threshold"/> of the cam (its gold frame included), it moves to the
/// free slot nearest home; at most one move per <see cref="MinGapMs"/> per cam.</item>
/// <item>It goes home only after home has been clear for <see cref="HomeClearMs"/>.</item>
/// <item>The two cams never share space (each keeps <see cref="CamGap"/> px from the other's slot).</item>
/// <item>No free slot: the cam is raised above the overlay (and put back under it once it is clear again).</item>
/// </list>
/// </summary>
public static class CamPlacer
{
    public const float Threshold = 0.02f, FramePad = 6f, CamGap = 16f;
    public const double HomeClearMs = 3000, MinGapMs = 1500;

    /// <summary>One cam's state (the OBS side keeps one per scene item).</summary>
    public sealed class Cam
    {
        public string Name = "";
        public SKRect Home;
        public SKRect[] Alts = Array.Empty<SKRect>();
        /// <summary>Where it is now (or is sliding to).</summary>
        public SKRect Cur;
        public double LastMoveMs = -1e12, HomeFreeSince = -1;
        public bool Raised;
        public bool AtHome => Near(Cur, Home);
    }

    public enum Act { None, Move, Raise, Lower }

    /// <summary>The share of <paramref name="r"/> (grown by the frame) the graphics cover, 0..1 (overlapping graphics can
    /// count twice - fine for a yes / no at 2 %).</summary>
    public static float Cover(SKRect r, ReadOnlySpan<SKRect> g)
    {
        var f = new SKRect(r.Left - FramePad, r.Top - FramePad, r.Right + FramePad, r.Bottom + FramePad);
        float area = f.Width * f.Height, hit = 0;
        if (area <= 0) return 0;
        for (int i = 0; i < g.Length; i++)
        {
            float l = Math.Max(f.Left, g[i].Left), t = Math.Max(f.Top, g[i].Top), rr = Math.Min(f.Right, g[i].Right), b = Math.Min(f.Bottom, g[i].Bottom);
            if (rr > l && b > t) hit += (rr - l) * (b - t);
        }
        return Math.Min(1f, hit / area);
    }

    static bool HitsOther(SKRect r, Cam[] cams, int n, int self)
    {
        for (int j = 0; j < n; j++)
        {
            if (j == self) continue;
            var o = cams[j].Cur;
            float m = FramePad * 2 + CamGap;
            if (r.Left < o.Right + m && r.Right > o.Left - m && r.Top < o.Bottom + m && r.Bottom > o.Top - m) return true;
        }
        return false;
    }

    public static bool Near(SKRect a, SKRect b, float tol = 2f) =>
        Math.Abs(a.Left - b.Left) <= tol && Math.Abs(a.Top - b.Top) <= tol && Math.Abs(a.Width - b.Width) <= tol && Math.Abs(a.Height - b.Height) <= tol;

    /// <summary>One cam's next step (call for each enabled cam of the scene every collection). <paramref name="to"/> is
    /// the slot to move to for <see cref="Act.Move"/>. The caller carries the action out and updates the cam
    /// (<see cref="Moved"/>, <see cref="Cam.Raised"/>).</summary>
    public static Act Decide(Cam[] cams, int n, int i, ReadOnlySpan<SKRect> graphics, double nowMs, out SKRect to)
    {
        var cam = cams[i];
        to = cam.Cur;
        float cover = Cover(cam.Cur, graphics);
        bool homeFree = Cover(cam.Home, graphics) <= Threshold && !HitsOther(cam.Home, cams, n, i);
        if (cam.AtHome || !homeFree) cam.HomeFreeSince = -1;
        else if (cam.HomeFreeSince < 0) cam.HomeFreeSince = nowMs;
        bool canMove = nowMs - cam.LastMoveMs >= MinGapMs;

        if (cover > Threshold)
        {
            if (canMove)
            {
                // the free slot nearest home (home itself first)
                if (!cam.AtHome && homeFree) { to = cam.Home; return Act.Move; }
                foreach (var s in cam.Alts)
                {
                    if (Near(s, cam.Cur)) continue;
                    if (Cover(s, graphics) <= Threshold && !HitsOther(s, cams, n, i)) { to = s; return Act.Move; }
                }
            }
            // nowhere to go (or it only just moved): above the overlay until it is clear
            return cam.Raised ? Act.None : Act.Raise;
        }
        if (cam.Raised) return Act.Lower;
        if (!cam.AtHome && cam.HomeFreeSince >= 0 && nowMs - cam.HomeFreeSince >= HomeClearMs && canMove) { to = cam.Home; return Act.Move; }
        return Act.None;
    }

    /// <summary>Records a move (the slide is the caller's job).</summary>
    public static void Moved(Cam cam, SKRect to, double nowMs) { cam.Cur = to; cam.LastMoveMs = nowMs; cam.HomeFreeSince = -1; }

    /// <summary>Same-size alternative slots for a cam at <paramref name="home"/> on a <paramref name="canvasW"/> x
    /// <paramref name="canvasH"/> canvas: the corners, edge middles and the home row / column, 48 px in from the edges
    /// and (at the top) under the session bar + ticker stack, nearest to home first, at most <paramref name="max"/>,
    /// none overlapping <paramref name="avoid"/> (the other cam's home) or each other much.</summary>
    public static SKRect[] Alternatives(SKRect home, float canvasW, float canvasH, ReadOnlySpan<SKRect> avoid, int max = 5)
    {
        float w = home.Width, h = home.Height, m = 48f * canvasW / 3840f, topY = 340f * canvasH / 2160f;
        Span<float> xs = stackalloc float[] { m, home.Left, (canvasW - w) / 2f, canvasW - m - w };
        Span<float> ys = stackalloc float[] { topY, home.Top, (canvasH - h) / 2f, canvasH - m - h };
        var list = new List<(float D, SKRect R)>();
        foreach (var x in xs)
            foreach (var y in ys)
            {
                var r = SKRect.Create(x, y, w, h);
                if (r.Left < 0 || r.Top < 0 || r.Right > canvasW || r.Bottom > canvasH) continue;
                if (IoU(r, home) > 0.3f) continue;
                bool bad = false;
                foreach (var a in avoid) if (r.IntersectsWith(new SKRect(a.Left - CamGap, a.Top - CamGap, a.Right + CamGap, a.Bottom + CamGap))) { bad = true; break; }
                if (bad) continue;
                float dx = r.MidX - home.MidX, dy = r.MidY - home.MidY;
                list.Add((dx * dx + dy * dy, r));
            }
        list.Sort((a, b) => a.D.CompareTo(b.D));
        var outL = new List<SKRect>();
        foreach (var (_, r) in list)
        {
            bool dup = false;
            foreach (var o in outL) if (IoU(o, r) > 0.5f) { dup = true; break; }
            if (dup) continue;
            outL.Add(r);
            if (outL.Count >= max) break;
        }
        return outL.ToArray();
    }

    static float IoU(SKRect a, SKRect b)
    {
        var i = SKRect.Intersect(a, b);
        if (i.IsEmpty) return 0;
        float ia = i.Width * i.Height;
        return ia / (a.Width * a.Height + b.Width * b.Height - ia);
    }
}
