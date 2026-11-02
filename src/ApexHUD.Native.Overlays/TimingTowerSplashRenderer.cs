using System.Globalization;
using System.IO;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Branded splash transition that covers the tower lane while a scene rebuild
/// happens behind it. Phase 1: an animated class-coloured background with a
/// centred vertical composite (iRacing wordmark on top, series logo, class
/// name). Phase 2 will replace <see cref="DrawBackground"/> with decoded video
/// frames; the centred composite stays identical, which is the seam.
///
/// <para>Driven entirely by <see cref="SceneDirector"/>: reads
/// <see cref="SceneDirector.SplashAlpha"/> for the fade and
/// <see cref="SceneDirector.SplashScene"/> for the announced class. Renders
/// nothing when no splash is active.</para>
/// </summary>
public sealed class TimingTowerSplashRenderer : IDisposable
{
    // Composite geometry inside the splash. The renderer centres horizontally
    // at originX + width/2 and stacks vertically around the lane's vertical
    // centre. All sizes are tower-lane-scaled (lane ≈ 432 px wide).
    const float WordmarkDisplayHeight = 48f;   // iRacing wordmark target height
    const float WordmarkMaxWidth      = 320f;  // cap for very wide wordmarks
    const float SeriesLogoDisplayHeight = 96f;
    const float SeriesLogoMaxWidth      = 360f;
    const float StackGapAfterWordmark = 28f;   // wordmark bottom -> series top
    const float StackGapAfterSeries   = 36f;   // series bottom -> class name top
    const float ClassNameTextSize     = 72f;
    const float ClassLogoDisplayHeight = 72f;   // class logo target height (matches the name text)
    const float ClassLogoMaxWidth      = 360f;  // width cap; height scales down to keep aspect

    SKBitmap? wordmarkBitmap;
    readonly Dictionary<string, SKBitmap?> seriesLogoCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, SKBitmap?> classLogoCache = new(StringComparer.OrdinalIgnoreCase);

    // Background decode-ahead streamer for the active transition clip. Frames
    // are decoded off the render thread so 30 fps full-resolution playback stays
    // smooth (synchronous decode on the render thread was the GTP choppiness).
    readonly FrameStreamer frameStreamer = new();
    string? streamingClipKey;

    // Reused paints/shader for the video path so the hot per-frame draw makes no
    // allocations (SkiaSharp guidance: never build paints or shaders inside the
    // render loop). The scrim depends only on lane geometry, which is constant
    // for the life of the window, so it is built once and cached.
    readonly SKPaint videoFramePaint = new() { FilterQuality = SKFilterQuality.Low, IsAntialias = false };
    SKImage? lastFrameImg; SKBitmap? lastFrameBmp; int lastFrameIdx = -1;
    readonly SKPaint logoPaint = new() { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
    SKPaint? classNamePaint;
    SKPaint? scrimPaint;
    float scrimW = -1f, scrimH = -1f;
    // Cached fallback-background shaders (LMP2 / GTD Pro / any unmapped scene).
    // Rebuilding these every frame made the procedural background clunky; they
    // depend only on lane geometry (+ class colour for the bloom), so build once
    // and reuse. The sweep is a single cached gradient drawn via a per-frame
    // canvas translate, so it animates without reallocation.
    SKPaint? bloomPaint;
    float bloomW = -1f, bloomH = -1f;
    SKColor bloomColor;
    SKPaint? sweepPaint;
    float sweepBandHalf = -1f;

    public TimingTowerSplashRenderer()
    {
        TryLoadWordmark();
    }

    /// <summary>Draws the splash. No-op when no splash is active or alpha is zero.</summary>
    public void Draw(SKCanvas canvas, float originX, float originY, float width, float height,
                     RaceSnapshot snapshot, double nowMs)
    {
        // Wipe in/out (2026-10-18): the video still plays in full; instead of a
        // soft cross-fade, a hard edge sweeps LEFT->RIGHT to bring it in and
        // LEFT->RIGHT again to take it away (uncovering the new board). Drawn with a
        // clip - no SaveLayer/offscreen, so the in/out is also cheaper.
        var (revTop, revBottom) = SceneDirector.SplashReveal(nowMs);
        if (revBottom - revTop <= 0.001f) { ReleaseStreamer(); return; }
        var scene = SceneDirector.SplashScene;
        if (scene == null) { ReleaseStreamer(); return; }
        var info = InfoFor(scene, snapshot);
        var classColor = info.Color;

        // Horizontal wipe (Angelo, 2026-10-18): the reveal fractions run LEFT->RIGHT.
        // In: the video sweeps in from the left. Out: it sweeps away to the right,
        // uncovering the new board from the left.
        float clipLeft = originX + revTop * width;
        float clipRight = originX + revBottom * width;
        bool full = revTop <= 0.0001f && revBottom >= 0.9999f;

        canvas.Save();
        if (!full) canvas.ClipRect(new SKRect(clipLeft, originY, clipRight, originY + height));
        // 2026-10-24: scenes with no frame sequence (the public build, or "Built-in animated" in the
        // control app) get the built-in animated stinger, which draws its own class panel. Frame
        // sequences (the owner's footage / a user's rollers) keep the original path exactly.
        bool legacy = info.Legacy;   // owner's layout: classes without footage keep the old splash
        if (info.ClipKey == null && !legacy && !stingerBroken)
        {
            ReleaseStreamer();
            try
            {
                stinger.Draw(canvas, new SKRect(originX, originY, originX + width, originY + height), scene, classColor,
                             Math.Max(0, SceneDirector.SplashElapsedMs(nowMs)), SceneDirector.CurrentSplashDurationMs,
                             wordmarkBitmap, info.Series, info.ClassLogo);
            }
            catch
            {
                stingerBroken = true;   // never again this run: fall back to the Phase 1 splash below
            }
        }
        if (info.ClipKey != null || legacy || stingerBroken)
        {
            DrawBackground(canvas, originX, originY, width, height, scene, classColor, nowMs);
            DrawComposite(canvas, originX, originY, width, height, scene, classColor, snapshot);
        }
        canvas.Restore();

        // Leading edge of the wipe: a bright class-coloured vertical line with a
        // soft glow - on the right edge while wiping in, the left edge while wiping out.
        if (!full)
        {
            float edgeX = revBottom < 0.9999f ? clipRight : clipLeft;
            // the stinger lights very dark classes (GTP black) in silver; footage keeps the class colour
            var edge = info.ClipKey == null && !legacy ? CarArt.LightColor(classColor) : classColor;
            wipeEdgeGlow.Color = edge.WithAlpha(90);
            wipeEdgeLine.Color = edge;
            canvas.DrawRect(new SKRect(edgeX - 5f, originY, edgeX + 5f, originY + height), wipeEdgeGlow);
            canvas.DrawRect(new SKRect(edgeX - 1.5f, originY, edgeX + 1.5f, originY + height), wipeEdgeLine);
        }
    }

    /// <summary>2026-10-30 (class transitions choppy): the per-splash lookups (clip key, owner's-layout check, colour,
    /// logos) resolved once per announced scene instead of on every frame (each one built strings).</summary>
    sealed record SceneInfo(Scene Scene, int? SeriesId, string? ClipKey, bool Legacy, SKColor Color, SKBitmap? Series, SKBitmap? ClassLogo);
    SceneInfo? sceneInfo;

    SceneInfo InfoFor(Scene scene, RaceSnapshot snapshot)
    {
        var i = sceneInfo;
        if (i != null && ReferenceEquals(i.Scene, scene) && i.SeriesId == snapshot.SeriesId) return i;
        var clip = SceneTransitions.ClipKeyFor(scene);
        i = new SceneInfo(scene, snapshot.SeriesId, clip, SceneTransitions.UseLegacySplash(scene), ResolveSceneColor(scene),
                          LoadSeriesLogo(snapshot.SeriesId), LoadClassLogo(scene.ClassName));
        sceneInfo = i;
        return i;
    }

    /// <summary>2026-10-30: while the board holds, draws the next built-in roller's still parts in the background so it
    /// runs at full speed from its first frame (see <see cref="ProceduralStinger.Prepare"/>). Call with the same lane as
    /// <see cref="Draw"/>, on the render thread; does nothing while a splash plays or when the next board has footage.</summary>
    public void Prepare(SKCanvas canvas, float originX, float originY, float width, float height, RaceSnapshot snapshot)
    {
        if (stingerBroken || SceneDirector.SplashActive) return;
        var next = SceneDirector.UpcomingScene;
        if (next == null) return;
        if (next != prepScene || prepSeries != snapshot.SeriesId)
        {
            // value compare: the director rebuilds its scene list every tick
            prepScene = next; prepSeries = snapshot.SeriesId;
            prepOk = SceneTransitions.ClipKeyFor(next) == null && !SceneTransitions.UseLegacySplash(next);
            prepColor = ResolveSceneColor(next);
            prepSeriesLogo = LoadSeriesLogo(snapshot.SeriesId);
            prepClassLogo = LoadClassLogo(next.ClassName);
        }
        if (!prepOk) return;
        try
        {
            stinger.Prepare(canvas, new SKRect(originX, originY, originX + width, originY + height), next, prepColor,
                            wordmarkBitmap, prepSeriesLogo, prepClassLogo);
        }
        catch { /* the splash draws everything live then */ }
    }
    Scene? prepScene; int? prepSeries; bool prepOk; SKColor prepColor; SKBitmap? prepSeriesLogo, prepClassLogo;

    // The built-in animated class transition (2026-10-24) for scenes without a frame sequence.
    readonly ProceduralStinger stinger = new();
    bool stingerBroken;

    // Reused (no per-frame allocation) paints for the wipe's leading edge.
    readonly SKPaint wipeEdgeLine = new() { IsAntialias = true };
    readonly SKPaint wipeEdgeGlow = new()
    {
        IsAntialias = true,
        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3f),
    };

    // ----- background (Phase-1 animated / Phase-2 video seam) -----
    //
    // If the announced scene has a bundled transition clip (SceneTransitions),
    // draw the current frame of the real footage, cover-filling the lane, with a
    // dark legibility scrim so the white wordmark reads over bright footage.
    // Otherwise (LMP2, anything unmapped) draw the Phase 1 animated background:
    // a deep-glass base + a soft class-coloured radial bloom + a slow vertical
    // sweep. The centred composite (drawn separately) is identical either way —
    // that is the seam Phase 1 reserved.

    void DrawBackground(SKCanvas canvas, float originX, float originY,
                        float width, float height, Scene scene, SKColor classColor, double nowMs)
    {
        var rect = new SKRect(originX, originY, originX + width, originY + height);

        // Phase 2: real footage when a clip exists for this scene.
        if (TryDrawVideoFrame(canvas, rect, width, height, scene, nowMs))
            return;

        // Deep-glass base — same hex as TowerGlassBase so the splash visually
        // sits on the same surface family as the tower it's covering.
        using (var basePaint = new SKPaint { Color = ParseHex(BroadcastPalette.TowerGlassBase, 240) })
            canvas.DrawRect(rect, basePaint);
        Theme.Skin(canvas, rect, ThemeSurface.Splash, nowMs);   // #601: the broadcast theme (nothing on Default)

        // Class-coloured radial bloom centred on the lane.
        EnsureBloom(rect, width, height, classColor);
        canvas.DrawRect(rect, bloomPaint!);

        // Slow vertical sweep — a soft brightening band that travels top to
        // bottom on a ~4.5 s loop, giving the splash continuous motion without
        // ever distracting from the composite.
        double sweepPeriodMs = 4500;
        double phase = ((nowMs % sweepPeriodMs) / sweepPeriodMs); // 0..1
        float sweepY = originY + (float)phase * height;
        float sweepHalfWidth = height * 0.18f;
        // Cached gradient centred on local y=0; animate by translating the canvas
        // to the sweep position, clipped to the lane, so motion costs no allocation.
        EnsureSweep(sweepHalfWidth);
        canvas.Save();
        canvas.ClipRect(rect);
        canvas.Translate(0, sweepY);
        canvas.DrawRect(new SKRect(rect.Left, -sweepHalfWidth, rect.Right, sweepHalfWidth), sweepPaint!);
        canvas.Restore();

        // Thin class-coloured hairlines at the top + bottom of the lane —
        // signposts the splash is "on brand" without crowding the composite.
        using var hairlinePaint = new SKPaint
        {
            IsAntialias = false,
            Color = classColor.WithAlpha(180),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
        };
        canvas.DrawLine(rect.Left, rect.Top + 1, rect.Right, rect.Top + 1, hairlinePaint);
        canvas.DrawLine(rect.Left, rect.Bottom - 1, rect.Right, rect.Bottom - 1, hairlinePaint);
    }

    /// <summary>Draws the current transition-clip frame as the splash background
    /// when the scene has a bundled clip. Returns false (draw the animated
    /// background instead) when there is no clip or the frame can't be loaded.</summary>
    bool TryDrawVideoFrame(SKCanvas canvas, SKRect rect, float width, float height,
                           Scene scene, double nowMs)
    {
        var clipKey = SceneTransitions.ClipKeyFor(scene);
        if (clipKey == null)
        {
            // No clip for this scene (LMP2, unmapped). Make sure any previous
            // clip's streamer is released so it isn't left buffering.
            if (streamingClipKey != null) { frameStreamer.Stop(); streamingClipKey = null; }
            return false;
        }

        int frameCount = SceneTransitions.FrameCount(clipKey);
        if (frameCount <= 0) return false;

        // Start (or switch) the background streamer when the clip changes. This
        // decodes frame 0 inline so the first paint is instant, then streams the
        // rest ahead of playback on a worker thread.
        if (streamingClipKey != clipKey)
        {
            frameStreamer.Start(clipKey);
            streamingClipKey = clipKey;
        }

        double elapsed = SceneDirector.SplashElapsedMs(nowMs);
        if (elapsed < 0) elapsed = 0;
        int idx = (int)Math.Floor(elapsed / (1000.0 / SceneTransitions.Fps));
        idx = Math.Clamp(idx, 0, frameCount - 1);

        // Non-blocking: returns the ready frame, or the nearest earlier buffered
        // frame if the worker hasn't reached idx yet. Never decodes here.
        var frame = frameStreamer.GetFrame(clipKey, idx);
        if (frame == null || frame.Width <= 0 || frame.Height <= 0) return false;

        // Cover-fill the lane: scale to the larger ratio, centre, clip overflow.
        float scale = MathF.Max(width / frame.Width, height / frame.Height);
        float drawW = frame.Width * scale;
        float drawH = frame.Height * scale;
        float dx = rect.MidX - drawW / 2f;
        float dy = rect.MidY - drawH / 2f;
        var dst = new SKRect(dx, dy, dx + drawW, dy + drawH);

        canvas.Save();
        canvas.ClipRect(rect);
        // Frames are already ~1:1 with the lane on a 1080p primary (480x1042
        // into a 400x1048 lane), so Low (bilinear) is ample and far cheaper than
        // High's mipmap/bicubic per frame. Paint is reused, never allocated.
        // 2026-10-24 (4K perf): the clip runs at 30 fps but the overlay presents at 60, so each frame
        // was drawn twice - and a reused (mutable) frame buffer is re-sent to the graphics card on every
        // draw. Snapshot each new frame once as an image: the card keeps its copy for the second draw.
        if (!ReferenceEquals(frame, lastFrameBmp) || idx != lastFrameIdx || lastFrameImg == null)
        {
            lastFrameImg?.Dispose();
            lastFrameImg = SKImage.FromBitmap(frame);
            lastFrameBmp = frame; lastFrameIdx = idx;
        }
        if (lastFrameImg != null) canvas.DrawImage(lastFrameImg, dst, videoFramePaint);
        else canvas.DrawBitmap(frame, dst, videoFramePaint);
        canvas.Restore();

        // Dark radial scrim so the white wordmark / class name read over bright
        // footage (e.g. the GTP sunset). Built once and cached — the lane
        // geometry never changes for the life of the window, so rebuilding the
        // gradient every frame was pure allocation churn.
        EnsureScrim(rect, width, height);
        canvas.DrawRect(rect, scrimPaint!);

        return true;
    }

    /// <summary>Builds (once) and caches the legibility scrim for the current
    /// lane geometry, rebuilding only if the lane size ever changes.</summary>
    void EnsureScrim(SKRect rect, float width, float height)
    {
        if (scrimPaint != null && scrimW == width && scrimH == height) return;
        scrimPaint?.Dispose();
        float radius = MathF.Max(width, height) * 0.72f;
        var shader = SKShader.CreateRadialGradient(
            new SKPoint(rect.MidX, rect.MidY),
            radius,
            new[]
            {
                SKColors.Black.WithAlpha(150),
                SKColors.Black.WithAlpha(54),
                SKColors.Black.WithAlpha(0),
            },
            new[] { 0f, 0.55f, 1f },
            SKShaderTileMode.Clamp);
        scrimPaint = new SKPaint { IsAntialias = true, Shader = shader };
        scrimW = width;
        scrimH = height;
    }

    /// <summary>Builds (once) and caches the class-coloured radial bloom for the
    /// fallback background, rebuilding only when the lane geometry or class colour
    /// changes. Previously rebuilt every frame (the clunky procedural background).</summary>
    void EnsureBloom(SKRect rect, float width, float height, SKColor classColor)
    {
        if (bloomPaint != null && bloomW == width && bloomH == height && bloomColor == classColor) return;
        bloomPaint?.Dispose();
        float radius = MathF.Max(width, height) * 0.85f;
        var shader = SKShader.CreateRadialGradient(
            new SKPoint(rect.MidX, rect.MidY),
            radius,
            new[] { classColor.WithAlpha(92), classColor.WithAlpha(36), classColor.WithAlpha(0) },
            new[] { 0f, 0.45f, 1f },
            SKShaderTileMode.Clamp);
        bloomPaint = new SKPaint { IsAntialias = true, Shader = shader };
        bloomW = width; bloomH = height; bloomColor = classColor;
    }

    /// <summary>Builds (once) and caches the sweep band gradient, centred on local
    /// y=0 so the caller animates it with a canvas translate. Rebuilds only when
    /// the band half-height changes.</summary>
    void EnsureSweep(float halfWidth)
    {
        if (sweepPaint != null && sweepBandHalf == halfWidth) return;
        sweepPaint?.Dispose();
        var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, -halfWidth),
            new SKPoint(0, halfWidth),
            new[] { SKColors.White.WithAlpha(0), SKColors.White.WithAlpha(22), SKColors.White.WithAlpha(0) },
            new[] { 0f, 0.5f, 1f },
            SKShaderTileMode.Clamp);
        sweepPaint = new SKPaint { IsAntialias = true, Shader = shader };
        sweepBandHalf = halfWidth;
    }

    // ----- composite -----

    void DrawComposite(SKCanvas canvas, float originX, float originY,
                       float width, float height, Scene scene, SKColor classColor,
                       RaceSnapshot snapshot)
    {
        float cx = originX + width / 2f;
        float midY = originY + height / 2f;

        // Pre-resolve sizes for the three stack elements (wordmark, series
        // logo, class name) so the whole stack can be centred vertically.
        float wordmarkHeight = wordmarkBitmap != null ? WordmarkDisplayHeight : 0f;
        float wordmarkWidth = 0f;
        if (wordmarkBitmap != null)
            wordmarkWidth = MathF.Min(WordmarkMaxWidth, FitWidth(wordmarkBitmap, WordmarkDisplayHeight));

        var seriesBitmap = LoadSeriesLogo(snapshot.SeriesId);
        float seriesHeight = seriesBitmap != null ? SeriesLogoDisplayHeight : 0f;
        float seriesWidth = 0f;
        if (seriesBitmap != null)
            seriesWidth = MathF.Min(SeriesLogoMaxWidth, FitWidth(seriesBitmap, SeriesLogoDisplayHeight));

        float gapAfterWordmark = wordmarkBitmap != null ? StackGapAfterWordmark : 0f;
        float gapAfterSeries   = seriesBitmap != null
            ? StackGapAfterSeries
            : (wordmarkBitmap != null ? StackGapAfterWordmark : 0f);

        // Class element: bundled IMSA class logo (sized by width so its very
        // wide wordmark keeps aspect), else the big class-name text.
        var classLogoBitmap = LoadClassLogo(scene.ClassName);
        float classWidth = 0f;
        float classHeight = ClassNameTextSize;
        if (classLogoBitmap != null)
        {
            classWidth = MathF.Min(ClassLogoMaxWidth, FitWidth(classLogoBitmap, ClassLogoDisplayHeight));
            classHeight = classWidth * classLogoBitmap.Height / classLogoBitmap.Width;
        }

        float totalStackHeight = wordmarkHeight + gapAfterWordmark
                               + seriesHeight + gapAfterSeries
                               + classHeight;
        float stackTop = midY - totalStackHeight / 2f;

        // iRacing wordmark (always centred at the top of the stack when bundled).
        if (wordmarkBitmap != null)
        {
            var dst = new SKRect(
                cx - wordmarkWidth / 2f,
                stackTop,
                cx + wordmarkWidth / 2f,
                stackTop + wordmarkHeight);
            canvas.DrawBitmap(wordmarkBitmap, dst, logoPaint);
        }

        // Series logo (omitted gracefully when unknown).
        float seriesTop = stackTop + wordmarkHeight + gapAfterWordmark;
        if (seriesBitmap != null)
        {
            var dst = new SKRect(
                cx - seriesWidth / 2f,
                seriesTop,
                cx + seriesWidth / 2f,
                seriesTop + seriesHeight);
            canvas.DrawBitmap(seriesBitmap, dst, logoPaint);
        }

        // Class element: the IMSA class logo when one resolves, else the big
        // class-coloured name ("OVERALL" stays text in warm ivory).
        float classNameTop = seriesTop + seriesHeight + gapAfterSeries;
        if (classLogoBitmap != null)
        {
            var dst = new SKRect(
                cx - classWidth / 2f,
                classNameTop,
                cx + classWidth / 2f,
                classNameTop + classHeight);
            canvas.DrawBitmap(classLogoBitmap, dst, logoPaint);
        }
        else
        {
            var nameColor = scene.Focus == SceneFocus.Overall
                ? ParseHex(BroadcastPalette.TextPrimary)
                : classColor;
            classNamePaint ??= new SKPaint
            {
                IsAntialias = true,
                Typeface = Fonts.Inter.Bold,
                TextSize = ClassNameTextSize,
                TextAlign = SKTextAlign.Center,
            };
            classNamePaint.Color = nameColor;
            var namePaint = classNamePaint;
            float baseline = classNameTop + ClassNameTextSize * 0.78f;
            string title = scene.Focus == SceneFocus.Pits ? scene.Label.ToUpperInvariant() + "  PIT STOPS" : scene.Label.ToUpperInvariant();
            TextTracked.Draw(canvas, namePaint, title, cx, baseline, 0.06f * ClassNameTextSize);
        }
    }

    // ----- helpers -----

    /// <summary>Stop the background streamer and free its buffered frames.
    /// Called when no splash is active so nothing is left decoding between
    /// transitions.</summary>
    void ReleaseStreamer()
    {
        if (streamingClipKey != null)
        {
            frameStreamer.Stop();
            streamingClipKey = null;
        }
    }

    static float FitWidth(SKBitmap bmp, float displayHeight)
    {
        if (bmp.Height <= 0) return displayHeight;
        return bmp.Width * (displayHeight / bmp.Height);
    }

    static SKColor ResolveSceneColor(Scene scene)
    {
        if (scene.Focus == SceneFocus.Overall || string.IsNullOrEmpty(scene.ClassName))
            return ParseHex(BroadcastPalette.Leader); // OVERALL uses gold
        var hex = string.IsNullOrEmpty(scene.ClassColor)
            ? BroadcastPalette.Class(scene.ClassName!)
            : scene.ClassColor;
        return ParseHex(hex);
    }

    void TryLoadWordmark()
    {
        try
        {
            var path = Assets.Resolve("logos", "iracing-brand", "iRacing-Inline-Color-White.png");
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                wordmarkBitmap = Assets.Decode(stream);
            }
        }
        catch
        {
            wordmarkBitmap = null;
        }
    }

    SKBitmap? LoadSeriesLogo(int? seriesId)
    {
        var slug = SeriesNames.ResolveLogoSlug(seriesId);
        if (slug == null) return null;
        if (seriesLogoCache.TryGetValue(slug, out var cached)) return cached;

        SKBitmap? bmp = null;
        try
        {
            var path = Assets.Resolve("logos", "series", slug + ".png");
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                bmp = Assets.Decode(stream);
            }
        }
        catch
        {
            bmp = null;
        }
        seriesLogoCache[slug] = bmp;
        return bmp;
    }

    SKBitmap? LoadClassLogo(string? classLabel)
    {
        var slug = ClassLogo.Resolve(classLabel);
        if (slug == null) return null;
        if (classLogoCache.TryGetValue(slug, out var cached)) return cached;

        SKBitmap? bmp = null;
        try
        {
            var path = Assets.Resolve("logos", "classes", slug + ".png");
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                bmp = Assets.Decode(stream);
            }
        }
        catch
        {
            bmp = null;
        }
        classLogoCache[slug] = bmp;
        return bmp;
    }

    static SKColor ParseHex(string hex, byte alpha = 255)
    {
        var s = hex.AsSpan();
        if (s.Length > 0 && s[0] == '#') s = s[1..];
        if (s.Length != 6) return new SKColor(255, 255, 255, alpha);
        byte r = byte.Parse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte g = byte.Parse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte b = byte.Parse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new SKColor(r, g, b, alpha);
    }

    public void Dispose()
    {
        frameStreamer.Dispose();
        stinger.Dispose();
        videoFramePaint.Dispose();
        logoPaint.Dispose();
        classNamePaint?.Dispose();
        scrimPaint?.Dispose();
        bloomPaint?.Dispose();
        sweepPaint?.Dispose();
        wordmarkBitmap?.Dispose();
        wordmarkBitmap = null;
        foreach (var b in seriesLogoCache.Values) b?.Dispose();
        seriesLogoCache.Clear();
        foreach (var b in classLogoCache.Values) b?.Dispose();
        classLogoCache.Clear();
    }
}
