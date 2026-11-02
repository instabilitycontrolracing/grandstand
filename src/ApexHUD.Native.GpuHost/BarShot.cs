using System.IO;
using ApexHUD.Native.Overlays;
using SkiaSharp;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// Offline render of the session bar (normal + stretched) to a PNG for design review, from the canned
/// demo race. Invoked with <c>--bar-shot [out.png]</c>. CPU raster only; no window.
/// </summary>
internal static class BarShot
{
    public static int Run(string[] args)
    {
        var snap = CannedSnapshot.Build() with { TrackCountryCode = "fr" };
        const float scale = 1.5f;
        int W = 3840;
        float stretched = W / scale;
        int H = (int)(SessionBarRenderer.BarHeight * scale * 2 + 120);
        var info = new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var c = surface.Canvas;
        using (var bg = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(W, H),
                   new[] { new SKColor(70, 82, 70), new SKColor(120, 125, 118) }, null, SKShaderTileMode.Clamp) })
            c.DrawRect(new SKRect(0, 0, W, H), bg);
        using (var r = new SessionBarRenderer())
        {
            float nx = (W - SessionBarRenderer.BarWidth * scale) / 2f;
            for (int i = 0; i < 3; i++) r.Draw(c, nx, 20, scale, snap, DateTime.Now, 20000 + i * 16);
            for (int i = 0; i < 3; i++) r.Draw(c, 0, 60 + SessionBarRenderer.BarHeight * scale, scale, snap, DateTime.Now, 20000 + i * 1500, stretched);
        }
        c.Flush();
        string outPath = args.SkipWhile(a => !string.Equals(a, "--bar-shot", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault()
                         ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "bar_shot.png");
        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 95);
        File.WriteAllBytes(outPath, data.ToArray());
        Console.WriteLine(outPath);
        return 0;
    }
}
