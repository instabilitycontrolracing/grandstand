using SkiaSharp;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// Phase C, requirement D(4) — the seam for uploading video frames into a
/// <b>reused</b> GPU texture AHEAD of draw, instead of letting
/// <c>SKCanvas.DrawBitmap(SKBitmap)</c> upload a transient CPU bitmap to a
/// throwaway GPU texture on every frame.
///
/// <para>It keeps one persistent, budgeted GPU <see cref="SKSurface"/> sized to
/// the clip's frame geometry. <see cref="Upload"/> draws the next CPU frame into
/// that surface (a single CPU→GPU copy into already-allocated GPU memory) and
/// returns a GPU-resident <see cref="SKImage"/> ready to be drawn. While the
/// geometry is stable — the normal case for a video clip — the GPU memory is
/// reused, so there is no per-frame texture allocation.</para>
///
/// <para><b>Status.</b> Minor for the 480 px tower splash, whose frames are ~1:1
/// with the lane and draw cheaply from the CPU bitmap today (that faithful path
/// is unchanged this session). This uploader is the REQUIRED-ready seam for the
/// future full-size sponsor-card videos, where the per-frame upload of a
/// screen-sized frame is the cost that must be pre-staged. It is validated once
/// on the host's real GL context at startup (<see cref="Validate"/>) so we know
/// the path works on Angelo's driver before a feature phase wires it into a
/// renderer.</para>
/// </summary>
internal sealed class GpuFrameUploader : IDisposable
{
    GRContext? context;
    SKSurface? gpuSurface;
    int width, height;

    /// <summary>
    /// Upload <paramref name="frame"/> into the reused GPU surface and return a
    /// GPU-resident image for drawing. Recreates the surface only when the
    /// context or frame geometry changes; otherwise the GPU memory is reused.
    /// </summary>
    public SKImage? Upload(GRContext ctx, SKBitmap frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return null;

        if (gpuSurface == null || width != frame.Width || height != frame.Height
            || !ReferenceEquals(context, ctx))
        {
            gpuSurface?.Dispose();
            context = ctx;
            width = frame.Width;
            height = frame.Height;
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            gpuSurface = SKSurface.Create(ctx, budgeted: true, info);
            if (gpuSurface == null) return null;
        }

        var canvas = gpuSurface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(frame, 0, 0);   // CPU -> reused GPU target
        gpuSurface.Flush();
        return gpuSurface.Snapshot();     // GPU-resident; ready to draw
    }

    /// <summary>
    /// One-time validation that the reused-GPU-texture upload path works on the
    /// host's actual GL context. Returns true and a short detail string. Used at
    /// startup so a broken driver path is visible in the log immediately rather
    /// than only when a feature phase first relies on it.
    /// </summary>
    public static bool Validate(GRContext ctx, out string detail)
    {
        try
        {
            using var up = new GpuFrameUploader();
            using var bmp = new SKBitmap(8, 8);
            bmp.Erase(SKColors.White);
            using var img = up.Upload(ctx, bmp);
            detail = img != null ? $"ready ({img.Width}x{img.Height} GPU image uploaded)" : "FAILED (no image)";
            return img != null;
        }
        catch (Exception ex)
        {
            detail = "FAILED: " + ex.Message;
            return false;
        }
    }

    public void Dispose()
    {
        gpuSurface?.Dispose();
        gpuSurface = null;
        context = null;
    }
}
