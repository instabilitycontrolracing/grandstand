// First-show timing tower bench (2026-10-27, #44). One process per mode (JIT is per process):
//   tower_warm cold   - the tower's first 30 frames with nothing warmed (the old behaviour)
//   tower_warm warm   - Prewarm + WarmCode on a background thread, WarmGpu once on this thread while the tower is
//                       hidden, then the same 30 frames
// Raster CPU surface at 3840x2160 (the TV), canvas scaled 2x like the host. The field is SYNTHETIC (40 made-up cars,
// 3 classes); logo pictures come from the assets folder next to the tool (placeholders here, the real ones on the PC).
using System.Diagnostics;
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

string mode = args.Length > 0 ? args[0] : "cold";
int frames = 30;

string[] brands = { "BMW", "Porsche", "Ferrari", "Mercedes", "Audi", "Lamborghini", "Aston", "Ford", "Mclaren", "Cadillac", "Acura" };
var classes = new List<DriverClass>();
int idx = 0;
foreach (var (cls, n, col) in new[] { ("GTP", 10, "#2B2B2B"), ("LMP2", 8, "#3B82F6"), ("GT3", 22, "#22C55E") })
{
    var ds = new List<Driver>();
    for (int i = 0; i < n; i++, idx++)
        ds.Add(new Driver(idx, $"Synthetic Driver {idx}", $"S. Driver{idx}", "SD", (idx + 1).ToString(), "Car", brands[idx % brands.Length], cls, col,
            "A 4.20", "#0153DB", idx + 1, i + 1, 6, 0.1f + 0.02f * idx, 5, 0, 95f + i * 0.3f, 96f + i * 0.3f,
            i == 0 ? null : i * 1.4, i == 0 ? null : 1.4, false, true, false, false, 2000 + 37 * idx));
    classes.Add(new DriverClass(cls, col, ds));
}
var all = classes.SelectMany(c => c.Drivers).ToList();
var snap = new RaceSnapshot(true, DateTime.UtcNow, 4, "Race", RacePhase.Broadcast, FlagState.Empty, 6, 30, 24, 3000, 50000, 20, 30, "C", false,
    "synthetic", "Synthetic Track", "Synthetic Track", null, null, all.Count, all[12].CarIdx, all[12].CarIdx, all[12], all[0], all[12], classes)
    { SessionNum = 2, SessionKey = "synthetic:2" };

PresentationState.Tick(snap);
Console.WriteLine($"mode {mode}: tower visible gate = {PresentationState.TowerVisible}");

var info = new SKImageInfo(3840, 2160, SKColorType.Rgba8888, SKAlphaType.Premul);
using var surface = SKSurface.Create(info);
var canvas = surface.Canvas;
var tower = new TimingTowerRenderer();
double now = 10000;

double Ms(long t0) => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

if (mode == "warm")
{
    long b0 = Stopwatch.GetTimestamp();
    var task = Task.Run(() => { TimingTowerRenderer.WarmCode(snap); tower.Prewarm(snap); });
    task.Wait();
    Console.WriteLine($"background warm-up (Prewarm + WarmCode): {Ms(b0):F1} ms - off the render thread");
    long l0 = Stopwatch.GetTimestamp();
    tower.WarmLayout(snap, now, 1080 - 34f);
    Console.WriteLine($"render-thread WarmLayout (no-draw canvas) while hidden: {Ms(l0):F1} ms");
    now += 5000;
    canvas.Clear(SKColors.Transparent);
    canvas.Save(); canvas.Scale(2f);
    long g0 = Stopwatch.GetTimestamp();
    tower.WarmGpu(canvas, 16f, 18f, 1080 - 34f, snap, now);
    canvas.Restore(); canvas.Flush();
    Console.WriteLine($"render-thread WarmGpu while hidden: {Ms(g0):F1} ms (on the GPU this is the texture/glyph upload; here raster)");
    now += 3000;   // the tower first shows a few seconds later
}

// the first Draw only starts the 220 ms fade (alpha 0, nothing painted); the fade frames paint through the layer
var cost = new List<double>(); var alloc = new List<long>();
for (int f = 0; f < frames; f++)
{
    canvas.Clear(SKColors.Transparent);
    canvas.Save(); canvas.Scale(2f);
    long a0 = GC.GetAllocatedBytesForCurrentThread();
    long t0 = Stopwatch.GetTimestamp();
    tower.Draw(canvas, 16f, 18f, 1080 - 34f, snap, now);
    canvas.Restore();
    canvas.Flush();
    cost.Add(Ms(t0)); alloc.Add(GC.GetAllocatedBytesForCurrentThread() - a0);
    now += 16.7;
}
Console.WriteLine("frame  ms      KB   (frame 0 starts the fade and paints nothing; 1-13 fade in through the layer)");
for (int f = 0; f < frames; f++) if (f < 6 || f == 14 || f == frames - 1) Console.WriteLine($"{f,5}  {cost[f],6:F1}  {alloc[f] / 1024.0,6:F1}");
var steady = cost.Skip(16).OrderBy(x => x).ToList();
double med = steady[steady.Count / 2];
Console.WriteLine($"FIRST PAINTED FRAME {cost[1]:F1} ms ({alloc[1] / 1024.0:F0} KB), fade frames 2-5 avg {cost.Skip(2).Take(4).Average():F1} ms, steady median {med:F1} ms -> one-off on first show ~{cost[1] - cost.Skip(2).Take(4).Average():F0} ms");
if (args.Contains("--png"))
{
    using var img = surface.Snapshot(new SKRectI(0, 0, 900, 2160));
    using var data = img.Encode(SKEncodedImageFormat.Png, 90);
    File.WriteAllBytes($"tower_{mode}.png", data.ToArray());
}
