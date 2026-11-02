using System.IO;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// The running Michelin Man on the pit-stop tower's stem (2026-10-27, #409): Angelo's own frame sequence in
/// Documents\iRacing\Grandstand\corner_logos\michelin_run\001.png.. (personal asset, never shipped; already turned so the
/// animation runs UP the stem; 15 fps; the last frame is the finished logo). Played once when the pit card comes in, then
/// frozen on the last frame.
/// Memory / render thread: nothing is loaded until the card first asks. Frames are decoded on a background thread a few
/// ahead of the one being shown, into a small ring (at most <see cref="Ring"/> frames, ~0.45 MB each); only the last frame is
/// kept for good. The render thread never touches the disk and never waits: if the wanted frame isn't decoded yet it gets
/// the nearest earlier one it has (or null). No allocations on the render thread in steady state.
/// </summary>
public static class MichelinRun
{
    /// <summary>Frames per second of the set: 15 by default; loop.txt's second number sets it (#442: a 60 fps stride).</summary>
    public static double Fps { get; private set; } = 15;
    const int Ahead = 4;
    static int Ring = 6;
    /// <summary>#439 (2026-10-27, Angelo: "loop it to stay in his running stride"): loop.txt in the frames folder holds the
    /// 1-based first frame of the stride cycle; after the last frame it goes back there and keeps running, forever. With a
    /// loop every frame is kept decoded (a short set: ~21 frames, ~0.6 MB each), so the wrap never waits.</summary>
    static int loopStart = -1;
    /// <summary>Preview tools: decode on the calling thread (deterministic renders). Never set in the app.</summary>
    public static bool Synchronous;

    static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "iRacing", "Grandstand", "corner_logos", "michelin_run");
    static readonly object gate = new();
    static string[]? files;                 // null = not looked yet
    static DateTime scannedAt = DateTime.MinValue;
    static SKImage?[] ring = new SKImage?[6];
    static int[] ringIdx = { -1, -1, -1, -1, -1, -1 };
    static SKImage? last;
    static readonly List<SKImage> retired = new();
    static volatile int want = -1;
    static bool working;

    /// <summary>Frames on disk (0 = none / not looked yet: the first call starts the look on a background thread).</summary>
    public static int Count
    {
        get
        {
            var f = files;
            if (f == null || (DateTime.UtcNow - scannedAt).TotalSeconds > 60) Scan();
            return files?.Length ?? 0;
        }
    }

    static void Scan()
    {
        lock (gate) { if (working) return; scannedAt = DateTime.UtcNow; working = true; }
        void Work()
        {
            string[] list;
            try { list = Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.png").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray() : Array.Empty<string>(); }
            catch { list = Array.Empty<string>(); }
            int ls = -1;
            try
            {
                var lf = Path.Combine(Folder, "loop.txt");
                if (File.Exists(lf))
                {
                    var parts = File.ReadAllText(lf).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0 && int.TryParse(parts[0], out var v) && v >= 1 && v < list.Length && list.Length <= 60) ls = v - 1;
                    Fps = parts.Length > 1 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) && f >= 5 && f <= 120 ? f : 15;
                }
                else Fps = 15;
            }
            catch { ls = -1; }
            lock (gate)
            {
                bool changed = files == null || files.Length != list.Length || ls != loopStart;
                files = list.Length >= 2 ? list : Array.Empty<string>();
                if (changed)
                {
                    for (int i = 0; i < Ring; i++) { if (ring[i] != null) retired.Add(ring[i]!); }
                    if (last != null) retired.Add(last); last = null;
                    loopStart = ls;
                    Ring = ls >= 0 ? Math.Max(6, files.Length) : 6;
                    ring = new SKImage?[Ring]; ringIdx = new int[Ring]; Array.Fill(ringIdx, -1);
                }
                working = false;
            }
        }
        if (Synchronous) Work(); else ThreadPool.QueueUserWorkItem(_ => Work());
    }

    /// <summary>The frame to show <paramref name="t"/> seconds into the card (frozen on the last), or null while it is
    /// still being read. Never blocks.</summary>
    public static SKImage? FrameAt(double t)
    {
        int n = Count;
        if (n == 0) return null;
        int raw = (int)Math.Floor(Math.Max(0, t) * Fps);
        int lsNow = loopStart;
        int idx = lsNow >= 0 && raw >= n ? lsNow + (raw - lsNow) % (n - lsNow) : Math.Clamp(raw, 0, n - 1);
        SKImage? best = null; int bestIdx = -1;
        lock (gate)
        {
            if (idx == n - 1 && last != null) return last;
            for (int i = 0; i < Ring; i++)
                if (ring[i] != null && ringIdx[i] <= idx && ringIdx[i] > bestIdx) { best = ring[i]; bestIdx = ringIdx[i]; }
            if (bestIdx == idx) { if (want != idx) Request(idx); return best; }
        }
        Request(idx);
        if (Synchronous) lock (gate) { if (idx == n - 1 && last != null) return last; for (int i = 0; i < Ring; i++) if (ringIdx[i] == idx) return ring[i]; }
        return best;
    }

    /// <summary>#505 (2026-10-29, Angelo: "when the Michelin pit stop card comes on the animation is very choppy until the
    /// information loads ... the running animation doesn't even run"): read the folder and decode the first frames (with a
    /// loop: all of them) in the background ahead of time, so the card never waits on the disk. True once there is
    /// something decoded. Safe to call every frame (it only starts work when needed).</summary>
    public static bool Prewarm()
    {
        if (Count == 0) return false;                     // the folder look itself runs in the background
        lock (gate) { if (ringIdx[0] >= 0 || ringIdx.Any(i => i >= 0)) return last != null; }
        Request(0);
        return false;
    }

    /// <summary>#505: true while the background decode is still working through the set.</summary>
    public static bool Busy { get { lock (gate) return working || NeedsWork(); } }

    /// <summary>#505: the frames decoded so far (for the host's invisible GPU warm-up draw).</summary>
    public static List<SKImage> Decoded()
    {
        var o = new List<SKImage>(Ring + 1);
        lock (gate) { for (int i = 0; i < Ring; i++) if (ring[i] != null) o.Add(ring[i]!); if (last != null) o.Add(last); }
        return o;
    }

    static void Request(int idx)
    {
        lock (gate)
        {
            want = idx;
            if (working) return;
            working = true;
        }
        if (Synchronous) Decode(); else ThreadPool.QueueUserWorkItem(_ => Decode());
    }

    // background: decode the wanted frame and a few after it (and the last frame once), retire what's behind
    static void Decode()
    {
        try
        {
            while (true)
            {
                string[]? f; int w;
                lock (gate) { f = files; w = want; }
                if (f == null || f.Length == 0) break;
                int target = -1;
                lock (gate)
                {
                    if (last == null) target = f.Length - 1;
                    else if (loopStart >= 0)
                    {
                        for (int k = 0; k < f.Length - 1; k++)                          // a loop: every frame, kept
                            if (Array.IndexOf(ringIdx, k) < 0) { target = k; break; }
                    }
                    else
                        for (int k = w; k < Math.Min(f.Length - 1, w + Ahead); k++)
                            if (Array.IndexOf(ringIdx, k) < 0) { target = k; break; }
                }
                if (target < 0) break;
                SKImage? img = null;
                try { using var bmp = SKBitmap.Decode(f[target]); if (bmp != null) { bmp.SetImmutable(); img = SKImage.FromBitmap(bmp); } } catch { img = null; }
                lock (gate)
                {
                    if (img == null) { files = Array.Empty<string>(); break; }   // an unreadable frame: give up (the still picture is used)
                    if (target == f.Length - 1) { last = img; continue; }
                    // the slot to reuse: an empty one, else the frame furthest from the window being shown (never one inside it -
                    // a replay of the card starts back at frame 0 while the ring still holds the end of the last showing)
                    int slot = -1, far = -1;
                    for (int i = 0; i < Ring; i++)
                    {
                        if (ringIdx[i] < 0) { slot = i; break; }
                        int ri = ringIdx[i];
                        if (ri >= w && ri < w + Ahead) continue;
                        int dist = ri < w ? (w - ri) + 1000 : ri - w;      // frames already passed go first
                        if (dist > far) { far = dist; slot = i; }
                    }
                    if (slot < 0) { img.Dispose(); break; }
                    if (ring[slot] != null) retired.Add(ring[slot]!);
                    ring[slot] = img; ringIdx[slot] = target;
                    // dispose frames retired a while ago (the render thread only ever draws the one it was just handed)
                    while (retired.Count > 3) { retired[0].Dispose(); retired.RemoveAt(0); }
                }
            }
        }
        finally { lock (gate) working = false; }
        // a newer frame was asked for while this pass ran: go again
        bool again;
        lock (gate) again = NeedsWork();
        if (again && !Synchronous) Request(want);
    }

    static bool NeedsWork()
    {
        var f = files;
        if (f == null || f.Length == 0) return false;
        if (last == null) return true;
        if (loopStart >= 0) { for (int k = 0; k < f.Length - 1; k++) if (Array.IndexOf(ringIdx, k) < 0) return true; return false; }
        for (int k = Math.Max(0, want); k < Math.Min(f.Length - 1, want + Ahead); k++) if (Array.IndexOf(ringIdx, k) < 0) return true;
        return false;
    }

    /// <summary>#436 (2026-10-27, Angelo: "it needs to fit the stem ... only the man, not the Michelin text"): the frames
    /// (tall, the man only - the logo's bar and lettering whited out) fill the stem's own shape: white inside
    /// <paramref name="shape"/>, the frame scaled to cover <paramref name="bounds"/> (centred, kept upright), clipped to the
    /// shape. False when there is no sequence (the caller falls back).</summary>
    public static bool DrawIn(SKCanvas c, SKPath shape, SKRect bounds, double t, float a, SKPaint paint, float fitW = 0, float centreX = float.NaN)
    {
        if (Count == 0) return false;
        var img = FrameAt(t);
        float al = Math.Clamp(a, 0, 1);
        c.Save();
        c.ClipPath(shape, antialias: true);
        panelFill.Color = SKColors.White.WithAlpha((byte)(255 * al));
        c.DrawRect(bounds, panelFill);
        if (img != null)
        {
            // #440 (Angelo: "his left arm sticks out weird"): fit the whole frame (arms included) into the stem's NARROW
            // width (fitW) so the tapering edge never cuts a hand off; full height only when that is narrower still
            float sc = fitW > 0 ? Math.Min(bounds.Height / img.Height, fitW / img.Width) : Math.Max(bounds.Width / img.Width, bounds.Height / img.Height);
            float dw = img.Width * sc, dh = img.Height * sc;
            float mx = float.IsNaN(centreX) ? bounds.MidX : centreX;
            paint.Color = SKColors.White.WithAlpha((byte)(255 * al));
            c.DrawImage(img, new SKRect(mx - dw / 2, bounds.MidY - dh / 2, mx + dw / 2, bounds.MidY + dh / 2), paint);
        }
        c.Restore();
        return true;
    }

    static readonly SKPaint panelFill = new() { IsAntialias = true };
    static readonly SKPaint panelEdge = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };

    /// <summary>Draws the white panel on the stem with the frame for card time <paramref name="t"/>, UPRIGHT (Angelo: "zoom
    /// it in, don't turn it on its side"): the panel is <paramref name="width"/> wide with its top at <paramref name="top"/>
    /// and the frame zoomed so its middle ~92% (where the action and the final logo sit) fills that width, cropped at the
    /// sides, never taller than <paramref name="maxHeight"/>. False when there is no sequence (the caller falls back).</summary>
    public static bool Draw(SKCanvas c, float cx, float top, float width, float maxHeight, double t, float a, SKPaint paint, SKColor edge)
    {
        if (Count == 0) return false;
        var img = FrameAt(t);
        float fw = img?.Width ?? 580, fh = img?.Height ?? 480;
        float dw = width / 0.92f, dh = dw * fh / fw;   // the logo spans ~90% of the frame: crop only a sliver at the sides
        if (dh > maxHeight) { dh = maxHeight; dw = dh * fw / fh; }
        float ph = dh;
        var panel = new SKRect(cx - width / 2, top, cx + width / 2, top + ph);
        float al = Math.Clamp(a, 0, 1);
        panelFill.Color = SKColors.White.WithAlpha((byte)(255 * al));
        c.DrawRect(panel, panelFill);
        if (img != null)
        {
            c.Save();
            c.ClipRect(panel);
            paint.Color = SKColors.White.WithAlpha((byte)(255 * al));
            c.DrawImage(img, new SKRect(cx - dw / 2, top, cx + dw / 2, top + dh), paint);
            c.Restore();
        }
        panelEdge.Color = edge.WithAlpha((byte)(255 * al));
        c.DrawRect(panel, panelEdge);
        return true;
    }
}
