using System.IO;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Background decode-ahead streamer for one transition clip at a time. The
/// render thread must never decode a JPEG mid-paint (that caused the GTP
/// choppiness — the heaviest clip couldn't decode inside the 33 ms frame
/// budget). Instead a single worker thread decodes frames slightly AHEAD of
/// playback into a small bounded buffer; the render thread only ever asks for
/// an already-decoded frame and gets it as a cheap reference.
///
/// <para><b>Bounded memory.</b> Only the active clip is ever streamed, and only
/// a look-ahead window of <see cref="LookAhead"/> frames is held decoded at
/// once. Frames strictly behind the current playback index are recycled as the
/// worker advances, so residency stays at roughly <see cref="LookAhead"/> frames
/// — not a whole clip. This preserves the project's low-RAM goal.</para>
///
/// <para><b>Buffer pool (Phase C, additive).</b> Decoded frames of a clip all
/// share one pixel geometry, so instead of allocating a fresh <see cref="SKBitmap"/>
/// per frame and disposing it on eviction (GC churn for the whole clip), evicted
/// frames are returned to a small free-pool and the next look-ahead decode reuses
/// one. This is minor for the 480 px splash today but is the seam the future
/// full-size sponsor-card videos need. Reuse is provably safe: the frame the
/// render thread most recently fetched is <see cref="pinnedIndex"/>-protected and
/// is never evicted or recycled, and a rented buffer is only ever filled for a
/// LOOK-AHEAD index that is not yet in the buffer (so the render thread cannot be
/// drawing it).</para>
///
/// <para><b>Threading.</b> Software <see cref="SKBitmap"/>s are not thread
/// affine, so decoding on the worker and drawing on the UI thread is safe. The
/// shared buffer, pool, and indices are guarded by <see cref="gate"/>.</para>
/// </summary>
public sealed class FrameStreamer : IDisposable
{
    /// <summary>Frames kept decoded ahead of the current playback index
    /// (~1.5 s at 30 fps). Wider than the Phase-B 1 s so a momentary worker stall
    /// can't drain the buffer into a hold/repeat mid-transition.</summary>
    const int LookAhead = 45;

    /// <summary>Hard cap on pooled (idle) frame buffers, so recycling can't grow
    /// residency beyond the look-ahead window's worth.</summary>
    const int MaxPool = LookAhead + 4;

    readonly object gate = new();
    readonly Dictionary<int, SKBitmap> buffer = new();
    readonly List<SKBitmap> pool = new(MaxPool);

    string? clipKey;
    string[] frames = Array.Empty<string>();   // the clip's frame paths, fixed for the whole playback (2026-10-24)
    int frameCount;
    int currentIndex;          // latest index the renderer has asked for
    int decodedUpTo = -1;      // highest contiguous index the worker has decoded
    int pinnedIndex = -1;      // the frame GetFrame last handed out (may still be drawing)
    bool stop;

    // Playback health counters (2026-10-18): unique frames shown on time vs held
    // because the decode-ahead hadn't reached them. Read + reset by the host log.
    public static int StatOnTime, StatHeld, StatBackward;
    int statLastIndex = -1;
    Thread? worker;

    /// <summary>Begin streaming a clip from frame 0. Decodes frame 0 inline
    /// (one cheap decode, ~3-8 ms) so the very first paint is instant, then
    /// hands the rest to the background worker. Idempotent for the same clip.</summary>
    public void Start(string key)
    {
        lock (gate)
        {
            if (clipKey == key && worker != null) return; // already streaming this clip
            StopLocked();

            clipKey = key;
            frames = SceneTransitions.Frames(key) ?? Array.Empty<string>();
            frameCount = frames.Length;
            currentIndex = 0;
            decodedUpTo = -1;
            pinnedIndex = -1;
            statLastIndex = -1;
            stop = false;

            // Synchronous frame 0 so the first paint never waits on the worker.
            var first = DecodeFrame(frames, 0);
            if (first != null)
            {
                buffer[0] = first;
                decodedUpTo = 0;
            }

            worker = new Thread(Run)
            {
                IsBackground = true,
                Name = "ApexHUD-FrameStreamer",
                // Lifted from BelowNormal (Phase B) to Normal so the look-ahead
                // stays full during the brief, decode-heavy transition window. The
                // worker only exists while a clip is playing, so it never competes
                // with the steady overlay render outside a transition.
                Priority = ThreadPriority.Normal,
            };
            worker.Start();
        }
    }

    /// <summary>Stop streaming and dispose every buffered + pooled frame.</summary>
    public void Stop()
    {
        lock (gate) StopLocked();
    }

    void StopLocked()
    {
        stop = true;
        worker = null;
        foreach (var b in buffer.Values) b.Dispose();
        buffer.Clear();
        foreach (var b in pool) b.Dispose();
        pool.Clear();
        decodedUpTo = -1;
        pinnedIndex = -1;
        clipKey = null;
        // We don't Join the worker (it's a background thread checking `stop`);
        // it observes stop and exits. A fresh Start creates a new worker.
    }

    /// <summary>Non-blocking fetch for the frame at <paramref name="index"/>.
    /// Returns the decoded frame if ready; otherwise the nearest earlier frame
    /// still buffered (so playback holds rather than stalls). Never decodes on
    /// the calling (render) thread.</summary>
    public SKBitmap? GetFrame(string key, int index)
    {
        lock (gate)
        {
            if (key != clipKey) return null;
            if (index < 0) index = 0;
            if (frameCount > 0 && index >= frameCount) index = frameCount - 1;

            bool newIndex = index != currentIndex || statLastIndex < 0;
            if (statLastIndex >= 0 && index < statLastIndex) StatBackward++;   // clip stepped BACK a frame (a "clunk")
            statLastIndex = index;
            currentIndex = index;

            if (buffer.TryGetValue(index, out var exact))
            {
                if (newIndex) StatOnTime++;
                pinnedIndex = index;     // protect the frame we are about to draw
                return exact;
            }
            if (newIndex) StatHeld++;   // loader hadn't reached this frame yet -> held

            // Not ready yet — return the most recent earlier frame we have so
            // the lane shows something rather than flashing empty.
            SKBitmap? best = null;
            int bestIdx = -1;
            foreach (var kv in buffer)
                if (kv.Key <= index && kv.Key > bestIdx) { best = kv.Value; bestIdx = kv.Key; }
            pinnedIndex = bestIdx;       // protect whatever we hand out (-1 if none)
            return best;
        }
    }

    void Run()
    {
        while (true)
        {
            string? key;
            string[] list;
            int target, count, evictBelow, pinned;
            lock (gate)
            {
                if (stop || clipKey == null) return;
                key = clipKey;
                list = frames;
                count = frameCount;
                target = Math.Min(currentIndex + LookAhead, count - 1);
                evictBelow = currentIndex - 1; // keep current and one behind
                pinned = pinnedIndex;
            }

            // Recycle frames well behind playback to bound memory — to the pool
            // when there's room (reused by the next decode), else disposed. The
            // pinned frame (possibly mid-draw on the render thread) is never
            // touched.
            lock (gate)
            {
                if (stop) return;
                if (evictBelow > 0)
                {
                    var toDrop = new List<int>();
                    foreach (var k in buffer.Keys)
                        if (k < evictBelow && k != pinned) toDrop.Add(k);
                    foreach (var k in toDrop)
                    {
                        var bmp = buffer[k];
                        buffer.Remove(k);
                        if (pool.Count < MaxPool) pool.Add(bmp);
                        else bmp.Dispose();
                    }
                }
            }

            // Decode the next missing frame in [currentIndex .. target].
            int next = -1;
            lock (gate)
            {
                if (stop || key != clipKey) return;
                for (int i = Math.Max(0, currentIndex); i <= target; i++)
                    if (!buffer.ContainsKey(i)) { next = i; break; }
            }

            if (next < 0)
            {
                // Caught up to the look-ahead window; sleep briefly and re-check.
                Thread.Sleep(4);
                continue;
            }

            // `next` is a look-ahead index not yet in the buffer, so the render
            // thread cannot be drawing it — safe to fill a recycled buffer here.
            var decoded = DecodeFrame(list, next);
            lock (gate)
            {
                if (stop || key != clipKey) { Recycle(decoded); return; }
                if (decoded != null && !buffer.ContainsKey(next))
                {
                    buffer[next] = decoded;
                    if (next == decodedUpTo + 1) decodedUpTo = next;
                }
                else Recycle(decoded);
            }
        }
    }

    /// <summary>Return a no-longer-needed bitmap to the pool (or dispose if full).
    /// Must be called under <see cref="gate"/>.</summary>
    void Recycle(SKBitmap? bmp)
    {
        if (bmp == null) return;
        if (pool.Count < MaxPool) pool.Add(bmp);
        else bmp.Dispose();
    }

    /// <summary>Decode a clip frame, reusing a pooled buffer of matching geometry
    /// when one is available (Phase C). Falls back to a plain allocate-on-decode
    /// for the first frames (empty pool) and on any codec error, so the output is
    /// byte-for-byte what <see cref="SKBitmap.Decode(System.IO.Stream)"/> produced
    /// before pooling.</summary>
    SKBitmap? DecodeFrame(string[] list, int index)
    {
        if (index < 0 || index >= list.Length) return null;
        var path = list[index];
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            if (codec == null) return null;

            var info = codec.Info;
            // Opaque JPEGs: a premultiplied/opaque buffer matches the legacy
            // SKBitmap.Decode result; normalise unknown alpha to Premul.
            if (info.AlphaType == SKAlphaType.Unknown)
                info = info.WithAlphaType(SKAlphaType.Premul);

            // Reuse a pooled buffer of the exact same geometry if we have one.
            SKBitmap? reuse = RentMatching(info);
            var bmp = reuse ?? new SKBitmap(info);

            var result = codec.GetPixels(info, bmp.GetPixels());
            if (result == SKCodecResult.Success || result == SKCodecResult.IncompleteInput)
            {
                // 2026-10-18 (video "clunk" fix): the codec writes straight into the
                // pixel memory of a REUSED pooled bitmap, which does not change its
                // generation ID - so the GPU texture cache (keyed on that ID) could
                // keep serving the OLD frame that buffer held before, making playback
                // jump back/stutter intermittently. Tell Skia the pixels changed.
                bmp.NotifyPixelsChanged();
                return FitLane(bmp);
            }

            // Codec path failed — discard and fall back to the legacy decode so we
            // never regress the picture.
            if (reuse != null) { lock (gate) Recycle(bmp); }
            else bmp.Dispose();
        }
        catch
        {
            // fall through to the legacy decode
        }

        try
        {
            using var stream = File.OpenRead(path);
            var legacy = SKBitmap.Decode(stream);
            return legacy == null ? null : FitLane(legacy);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>2026-10-24 (user rollers): frames bigger than the lane - a user's full-HD sequence - are
    /// shrunk and centre-cropped to 480 x 1046 here on the worker, so look-ahead memory stays bounded
    /// (45 frames x 2 MB, not 45 x 8 MB) and the render thread draws a lane-sized picture. The owner's
    /// lane-width footage (480 x 1042) is under the limit and passes through untouched.</summary>
    static SKBitmap FitLane(SKBitmap bmp)
    {
        const int LaneW = 480, LaneH = 1046;
        if (bmp.Width <= 640 && bmp.Height <= 1400) return bmp;
        float s = Math.Max(LaneW / (float)bmp.Width, LaneH / (float)bmp.Height);
        float cw = LaneW / s, ch = LaneH / s;
        var src = new SKRect((bmp.Width - cw) / 2f, (bmp.Height - ch) / 2f, (bmp.Width + cw) / 2f, (bmp.Height + ch) / 2f);
        var info = new SKImageInfo(LaneW, LaneH, SKColorType.Rgba8888, SKAlphaType.Premul);
        var dst = new SKBitmap(info);   // oversize user frames skip the buffer pool (two geometries would thrash it)
        using (var c = new SKCanvas(dst))
        using (var p = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = false })
        {
            c.Clear(SKColors.Black);
            c.DrawBitmap(bmp, src, new SKRect(0, 0, LaneW, LaneH), p);
        }
        dst.NotifyPixelsChanged();
        bmp.Dispose();
        return dst;
    }

    /// <summary>Pop a pooled bitmap whose geometry matches <paramref name="info"/>,
    /// or null. Pooled buffers from a prior clip of a different size are dropped.</summary>
    SKBitmap? RentMatching(SKImageInfo info)
    {
        lock (gate)
        {
            for (int i = pool.Count - 1; i >= 0; i--)
            {
                var b = pool[i];
                if (b.Width == info.Width && b.Height == info.Height && b.ColorType == info.ColorType)
                {
                    pool.RemoveAt(i);
                    return b;
                }
            }
            // Different geometry than the active clip — clear stale buffers so the
            // pool never pins memory for a size we no longer decode.
            if (pool.Count > 0 && (pool[0].Width != info.Width || pool[0].Height != info.Height))
            {
                foreach (var b in pool) b.Dispose();
                pool.Clear();
            }
        }
        return null;
    }

    public void Dispose() => Stop();
}
