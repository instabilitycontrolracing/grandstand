using System.IO;
using ApexHUD.Native.Common;
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

    /// <summary>#684: one bar per flag (each flag's light box), for design review. <c>--flag-shot [out.png]</c>.</summary>
    public static int Flags(string[] args)
    {
        var flags = new (string, uint)[] { ("yellow", 0x8), ("fcy", 0x4000), ("green", 0x4), ("white", 0x2), ("red", 0x10), ("blue", 0x20),
                                           ("black", 0x10000), ("meatball", 0x100000), ("checkered", 0x1), ("dq", 0x20000) };
        const float scale = 1.0f;
        int W = (int)SessionBarRenderer.BarWidth + 40, rowH = (int)SessionBarRenderer.BarHeight + 16;
        int H = rowH * flags.Length * 2 + 20;
        var info = new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var c = surface.Canvas;
        c.Clear(new SKColor(90, 96, 90));
        var basis = CannedSnapshot.Build() with { TrackCountryCode = "fr", Phase = RacePhase.Broadcast };
        int y = 10;
        foreach (var (name, bits) in flags)
            foreach (double at in new[] { 0.0, 450.0 })          // two moments of the animation
            {
                using var r = new SessionBarRenderer();
                var none = basis with { Flags = FlagState.FromBitField(0) };
                var fl = basis with { Flags = FlagState.FromBitField(bits) };
                using (var off = SKSurface.Create(info)) for (int i = 0; i < 2; i++) r.Draw(off.Canvas, 20, y, scale, none, DateTime.Now, 1000 + i * 16);
                double t0 = 2000;
                for (double t = t0; t < t0 + 400 + at; t += 16)
                    using (var off = SKSurface.Create(new SKImageInfo(8, 8))) r.Draw(off.Canvas, 20, y, scale, fl, DateTime.Now, t);
                r.Draw(c, 20, y, scale, fl, DateTime.Now, t0 + 400 + at);
                y += rowH;
            }
        c.Flush();
        string outPath = args.SkipWhile(a => !string.Equals(a, "--flag-shot", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault()
                         ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "flag_shot.png");
        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 95);
        File.WriteAllBytes(outPath, data.ToArray());
        Console.WriteLine(outPath);
        return 0;
    }
}
