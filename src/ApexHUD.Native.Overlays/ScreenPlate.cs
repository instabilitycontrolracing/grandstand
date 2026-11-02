using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>2026-10-30 (#604 perf pass 2): one costly draw - a blurred text glow, or a group of draws faded together
/// through a layer - kept as a picture at the screen's own pixel grid and re-drawn from it while nothing about it
/// changes. On Skia's GPU path every blur filter / SaveLayer is an extra offscreen pass on the graphics card each
/// frame; a still card does not need them again.
/// <para>Same look: the picture is drawn with the very same draw calls and the very same matrix (including where it
/// falls between pixels), on the host's graphics card when there is one (<see cref="GpuContext"/>), and put back at
/// its whole-pixel spot without resampling. A layer's opacity is applied when the picture is drawn, exactly as the
/// layer's own paint did. The picture is only made once the same draw has been asked for on two frames in a row,
/// so a card that is sliding or fading in draws live as before (no picture per frame).</para>
/// Render thread only.</summary>
internal sealed class ScreenPlate<TKey> : ScreenPlates.IPlate, IDisposable where TKey : struct, IEquatable<TKey>
{
    /// <summary>Draws the thing at its normal (local) coordinates. <paramref name="p"/> is a scratch paint the
    /// callback may reset and use (one at a time).</summary>
    public delegate void DrawFn(SKCanvas c, in TKey key, SKPaint p);

    /// <summary>Benches: false = always draw live (the old way).</summary>
    public static bool Enabled = true;

    readonly DrawFn draw;
    /// <summary>How the picture is put down (SrcOver; Overlay for the story cards' film grain, drawn on its own into
    /// the picture and blended as the live draw did).</summary>
    public SKBlendMode Blend = SKBlendMode.SrcOver;
    /// <summary>For <see cref="ScreenPlatePool{TKey}"/>: when this plate was last used, and for what.</summary>
    internal long Used;
    internal bool Holds(in TKey key) => (img != null && held.Key.Equals(key)) || (hasSeen && seen.Key.Equals(key));
    readonly SKPaint scratch = new(), layerPaint = new(), imgPaint = new();
    Req held, seen; bool hasSeen;
    SKImage? img;

    readonly record struct Req(TKey Key, float Sx, float Sy, float Fx, float Fy, int W, int H, SKRect Local, bool Layer);

    public ScreenPlate(DrawFn draw) { this.draw = draw; }
    long lastDrawMs; bool registered;

    /// <summary>A picture nobody has drawn for <see cref="ScreenPlates.IdleMs"/> is let go (graphics memory is shared with VR).</summary>
    void ScreenPlates.IPlate.DropIfIdle(long nowMs)
    {
        if (img != null && nowMs - lastDrawMs > ScreenPlates.IdleMs) { img.Dispose(); img = null; hasSeen = false; }
    }

    /// <summary>Draws <paramref name="key"/> (all of it inside <paramref name="local"/>). <paramref name="layer"/>:
    /// the draws are faded together like <c>c.SaveLayer(local, paint with alpha = layerAlpha)</c>.</summary>
    public void Draw(SKCanvas c, in TKey key, SKRect local, bool layer = false, byte layerAlpha = 255)
    {
        if (!registered) { registered = true; ScreenPlates.Register(this); }   // (on the thread that draws it)
        lastDrawMs = ScreenPlates.Tick();
        if (!Enabled || !TryPlace(c, key, local, layer, out var req, out int dx, out int dy))
        {
            Live(c, key, local, layer, layerAlpha);
            hasSeen = false;
            return;
        }
        if (img == null || !held.Equals(req))
        {
            bool stable = hasSeen && seen.Equals(req);
            seen = req; hasSeen = true;
            if (!stable || !Build(req))
            {
                Live(c, key, local, layer, layerAlpha);
                return;
            }
        }
        imgPaint.Color = SKColors.White.WithAlpha(layer ? layerAlpha : (byte)255);
        imgPaint.BlendMode = Blend;
        c.Save();
        c.ResetMatrix();
        c.DrawImage(img!, dx, dy, imgPaint);
        c.Restore();
    }

    void Live(SKCanvas c, in TKey key, SKRect local, bool layer, byte layerAlpha)
    {
        if (layer)
        {
            layerPaint.Color = new SKColor(255, 255, 255, layerAlpha);
            c.SaveLayer(local, layerPaint);
            draw(c, key, scratch);
            c.Restore();
        }
        else draw(c, key, scratch);
    }

    static bool TryPlace(SKCanvas c, in TKey key, SKRect local, bool layer, out Req req, out int dx, out int dy)
    {
        req = default; dx = dy = 0;
        var m = c.TotalMatrix;
        if (m.SkewX != 0 || m.SkewY != 0 || m.Persp0 != 0 || m.Persp1 != 0 || m.Persp2 != 1 || m.ScaleX <= 0 || m.ScaleY <= 0) return false;
        float l = local.Left * m.ScaleX + m.TransX, t = local.Top * m.ScaleY + m.TransY;
        float r = local.Right * m.ScaleX + m.TransX, b = local.Bottom * m.ScaleY + m.TransY;
        if (!float.IsFinite(l + t + r + b)) return false;
        dx = (int)MathF.Floor(l); dy = (int)MathF.Floor(t);
        int w = (int)MathF.Ceiling(r) - dx, h = (int)MathF.Ceiling(b) - dy;
        if (w <= 0 || h <= 0 || (long)w * h > 6_000_000) return false;
        req = new Req(key, m.ScaleX, m.ScaleY, m.TransX - dx, m.TransY - dy, w, h, local, layer);
        return true;
    }

    bool Build(in Req q)
    {
        var info = new SKImageInfo(q.W, q.H, SKColorType.Rgba8888, SKAlphaType.Premul);
        var ctx = GpuContext.Current;
        using var surf = (ctx != null ? SKSurface.Create(ctx, false, info) : null) ?? SKSurface.Create(info);
        if (surf == null) return false;
        var c = surf.Canvas;
        c.Clear(SKColors.Transparent);
        c.SetMatrix(new SKMatrix(q.Sx, 0, q.Fx, 0, q.Sy, q.Fy, 0, 0, 1));
        // (a layer's bounds - local, rounded out to whole pixels - are exactly this surface, so it clips the same)
        ScreenPlates.Building = true;
        try { draw(c, q.Key, scratch); } finally { ScreenPlates.Building = false; }
        c.Flush();
        img?.Dispose();
        img = surf.Snapshot();
        held = q;
        return img != null;
    }

    public void Dispose() { ScreenPlates.Unregister(this); img?.Dispose(); img = null; scratch.Dispose(); layerPaint.Dispose(); imgPaint.Dispose(); }
}

internal static class ScreenPlates
{
    /// <summary>True while a <see cref="ScreenPlate{TKey}"/> callback draws into its picture (not the screen).</summary>
    [ThreadStatic] public static bool Building;

    public interface IPlate { void DropIfIdle(long nowMs); }
    /// <summary>Pictures not drawn for this long are dropped (a card that left, a battle that ended).</summary>
    public const long IdleMs = 8000;
    static readonly List<(WeakReference<IPlate> P, int Thread)> all = new();
    [ThreadStatic] static long lastSweep;

    public static void Register(IPlate p) { lock (all) all.Add((new WeakReference<IPlate>(p), Environment.CurrentManagedThreadId)); }
    public static void Unregister(IPlate p) { lock (all) all.RemoveAll(w => !w.P.TryGetTarget(out var x) || ReferenceEquals(x, p)); }

    /// <summary>The clock for idle checks; sweeps idle pictures about once a second (render thread).</summary>
    public static long Tick()
    {
        long now = Environment.TickCount64;
        if (now - lastSweep > 1000)
        {
            lastSweep = now;
            int me = Environment.CurrentManagedThreadId;   // only this thread's pictures (each is drawn on one thread)
            lock (all)
                for (int i = all.Count - 1; i >= 0; i--)
                    if (!all[i].P.TryGetTarget(out var p)) all.RemoveAt(i);
                    else if (all[i].Thread == me) p.DropIfIdle(now);
        }
        return now;
    }
}

/// <summary>A few <see cref="ScreenPlate{TKey}"/>s for one kind of draw that can appear more than once a frame (two card
/// panels, a shadow per panel): each draw goes to the plate that already holds it, else to the one used longest ago.</summary>
internal sealed class ScreenPlatePool<TKey> : IDisposable where TKey : struct, IEquatable<TKey>
{
    readonly ScreenPlate<TKey>[] plates;
    long tick;

    public ScreenPlatePool(int n, ScreenPlate<TKey>.DrawFn draw, SKBlendMode blend = SKBlendMode.SrcOver)
    {
        plates = new ScreenPlate<TKey>[n];
        for (int i = 0; i < n; i++) plates[i] = new ScreenPlate<TKey>(draw) { Blend = blend };
    }

    public void Draw(SKCanvas c, in TKey key, SKRect local, bool layer = false, byte layerAlpha = 255)
    {
        ScreenPlate<TKey>? pick = null;
        foreach (var p in plates) if (p.Holds(key)) { pick = p; break; }
        if (pick == null) { pick = plates[0]; foreach (var p in plates) if (p.Used < pick.Used) pick = p; }
        pick.Used = ++tick;
        pick.Draw(c, key, local, layer, layerAlpha);
    }

    public void Dispose() { foreach (var p in plates) p.Dispose(); }
}

/// <summary>Blurred draws (a card's soft shadow, a bevel) as screen-grid pictures (#604 perf pass 2). The shape is
/// passed every frame but only drawn when the picture is (re)made; what identifies it is the caller's id plus the
/// numbers it was built from (the card rectangle, colour, blur, offset), so a shape must be a function of those.</summary>
internal static class BlurPlates
{
    public readonly struct Key : IEquatable<Key>
    {
        public readonly int Id; public readonly SKRect R; public readonly SKColor Color; public readonly float Sigma, Dx, Dy, Stroke;
        public readonly bool Mask, Aa;
        public readonly SKPath? Path;   // (not part of the identity: drawn only while building, in the same call)
        public readonly SKRect Rect; public readonly float Rx;   // a (round) rect instead of a path when Path is null
        public Key(int id, SKRect r, SKColor color, float sigma, bool mask, bool aa, float stroke, float dx, float dy, SKPath? path, SKRect rect, float rx)
        { Id = id; R = r; Color = color; Sigma = sigma; Mask = mask; Aa = aa; Stroke = stroke; Dx = dx; Dy = dy; Path = path; Rect = rect; Rx = rx; }
        public bool Equals(Key o) => Id == o.Id && R == o.R && Color == o.Color && Sigma == o.Sigma && Mask == o.Mask && Aa == o.Aa
                                     && Stroke == o.Stroke && Dx == o.Dx && Dy == o.Dy && Rect == o.Rect && Rx == o.Rx;
        public override bool Equals(object? obj) => obj is Key k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(Id, R, Color, Sigma, Dx, Dy);
    }

    [ThreadStatic] static ScreenPlatePool<Key>? pool;
    [ThreadStatic] static SKPaint? livePaint;
    static readonly Dictionary<float, SKImageFilter> imageBlurs = new();
    static readonly Dictionary<float, SKMaskFilter> maskBlurs = new();

    static void DrawShape(SKCanvas c, in Key k, SKPaint p)
    {
        p.IsAntialias = k.Aa; p.Color = k.Color;
        p.Style = k.Stroke > 0 ? SKPaintStyle.Stroke : SKPaintStyle.Fill; p.StrokeWidth = k.Stroke;
        if (k.Mask) { p.MaskFilter = MaskBlur(k.Sigma); p.ImageFilter = null; } else { p.ImageFilter = ImageBlur(k.Sigma); p.MaskFilter = null; }
        bool moved = k.Dx != 0 || k.Dy != 0;
        if (moved) { c.Save(); c.Translate(k.Dx, k.Dy); }
        if (k.Path != null) c.DrawPath(k.Path, p);
        else if (k.Rx > 0) c.DrawRoundRect(k.Rect, k.Rx, k.Rx, p);
        else c.DrawRect(k.Rect, p);
        if (moved) c.Restore();
        p.MaskFilter = null; p.ImageFilter = null;
    }

    static SKImageFilter ImageBlur(float s) { lock (imageBlurs) { if (!imageBlurs.TryGetValue(s, out var f)) imageBlurs[s] = f = SKImageFilter.CreateBlur(s, s); return f; } }
    static SKMaskFilter MaskBlur(float s) { lock (maskBlurs) { if (!maskBlurs.TryGetValue(s, out var f)) maskBlurs[s] = f = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, s); return f; } }

    /// <summary>Like <c>c.Save(); c.Translate(dx, dy); c.DrawPath(path, new SKPaint { Color, IsAntialias = aa,
    /// ImageFilter = blur(sigma) (or MaskFilter when <paramref name="mask"/>), Stroke width }); c.Restore()</c>.
    /// <paramref name="bounds"/>: the shape's bounds (before the offset).</summary>
    public static void Path(SKCanvas c, int id, SKRect r, SKPath path, SKRect bounds, SKColor color, float sigma, bool mask = false, bool aa = false,
                            float stroke = 0, float dx = 0, float dy = 0)
        => Draw(c, new Key(id, r, color, sigma, mask, aa, stroke, dx, dy, path, SKRect.Empty, 0), bounds);

    /// <summary>The same for a rect (rx = 0) or a round rect.</summary>
    public static void Rect(SKCanvas c, int id, SKRect rect, float rx, SKColor color, float sigma, bool mask = false, bool aa = false, float dx = 0, float dy = 0)
        => Draw(c, new Key(id, rect, color, sigma, mask, aa, 0, dx, dy, null, rect, rx), rect);

    static void Draw(SKCanvas c, in Key k, SKRect bounds)
    {
        if (k.Color.Alpha == 0) return;
        var b = bounds;
        b.Inflate(k.Sigma * 3f + k.Stroke / 2f + 2f, k.Sigma * 3f + k.Stroke / 2f + 2f);
        b.Offset(k.Dx, k.Dy);
        // only on the graphics card (where the picture gives the very same pixels); the CPU raster - the preview tools -
        // blurs in place exactly as before (an offscreen blur put down after differs there by a few 1/255ths)
        if (GpuContext.Current == null) { DrawShape(c, k, livePaint ??= new SKPaint()); return; }
        (pool ??= new ScreenPlatePool<Key>(6, DrawShape)).Draw(c, k, b);
    }
}

/// <summary>Paths that only depend on a card's rectangle (the Michelin tower's union of screen and pylon): the costly
/// path operation runs once per rectangle instead of every frame (#604 perf pass 2). Each call returns a NEW path copied
/// from the kept one - the caller disposes it. (A fresh path each frame, as before: Skia's GPU path keys its cached clip
/// and coverage masks on a path's identity, and handing it the very same path object every frame made the exit wipe
/// show the tower outside its clip - found by the story card bench.)</summary>
internal static class PathMemo
{
    [ThreadStatic] static Dictionary<(int, SKRect), SKPath>? paths;

    public static SKPath Get(int id, SKRect r, Func<SKRect, SKPath> build)
    {
        paths ??= new();
        if (!paths.TryGetValue((id, r), out var p))
        {
            if (paths.Count > 32) { foreach (var x in paths.Values) x.Dispose(); paths.Clear(); }
            paths[(id, r)] = p = build(r);
        }
        var copy = new SKPath();
        copy.AddPath(p);   // as the old code did with the union (Reset + AddPath): same points, same fill rule
        return copy;
    }
}
