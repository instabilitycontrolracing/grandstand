using System.IO;
using ApexHUD.Native.Overlays;
using SkiaSharp;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// Offline, windowless render of the <see cref="GridSpotlightRenderer"/> hero
/// cards to a PNG — a design-review path so we can see the cards without iRacing
/// being parked on the grid. Invoked with <c>--hero-shot</c>. CPU raster only;
/// no GL context or window is created.
/// </summary>
internal static class HeroShot
{
    public static int Run()
    {
        var snap = CannedSnapshot.Build();
        var classes = snap.ClassLeaderboard;
        var gtp = classes[0];
        var gt3 = classes[2];

        // Front-runner cards: GTP pole (Acura, whitened), GTP P2 (Cadillac,
        // whitened), GT3 pole (Chevrolet, colored) — a representative mix.
        var cards = new[] { gtp.Drivers[0], gtp.Drivers[1], gt3.Drivers[0] };

        const float margin = 28f, gap = 22f;
        int n = cards.Length;
        int w = (int)(GridSpotlightRenderer.CardWidth + margin * 2);
        int h = (int)(n * GridSpotlightRenderer.CardHeight + (n - 1) * gap + margin * 2);

        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var c = surface.Canvas;
        c.Clear(new SKColor(5, 7, 10, 255));

        using (var r = new GridSpotlightRenderer())
        {
            float y = margin;
            foreach (var d in cards)
            {
                r.Draw(c, margin, y, d);
                y += GridSpotlightRenderer.CardHeight + gap;
            }
        }
        c.Flush();

        // 2026-10-28 (distribution): the user's own Documents\iRacing, not one PC's Administrator profile
        string outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing");
        Directory.CreateDirectory(outDir);
        string outPath = Path.Combine(outDir, "hero_shot.png");
        using (var img = surface.Snapshot())
        using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
        using (var fs = File.OpenWrite(outPath))
            data.SaveTo(fs);

        Console.WriteLine("hero-shot -> " + outPath);
        return 0;
    }
}
