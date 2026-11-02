// #340 legibility previews (2026-10-27): the on-air graphics with small grey labels, drawn by the REAL renderers at
// 1920x1080 over a TV picture, one PNG per scene. Run once against the old build and once against the new build (run.sh,
// GSBIN) and compare - the file names are the same. Drivers are made up.
//   dotnet legibility_preview.dll OUT_DIR [background.png]
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

string outDir = args.Length > 0 ? args[0] : "out";
string? bgPath = args.Length > 1 && args[1].Length > 0 ? args[1] : null;
Directory.CreateDirectory(outDir);
StorySound.Enabled = false;
Fonts.WarmUp();
const int W = 1920, H = 1080;
SKBitmap? bg = bgPath != null && File.Exists(bgPath) ? SKBitmap.Decode(bgPath) : null;

void Background(SKCanvas c)
{
    if (bg != null) { using var p = new SKPaint { FilterQuality = SKFilterQuality.High }; c.DrawBitmap(bg, new SKRect(0, 0, W, H), p); }
    else c.Clear(new SKColor(70, 82, 74));
}

void Scene(string name, Action<SKCanvas> draw)
{
    try
    {
        using var surf = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surf.Canvas;
        Background(c);
        draw(c);
        using var img = surf.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 92);
        File.WriteAllBytes(Path.Combine(outDir, name + ".png"), data.ToArray());
        Console.WriteLine("ok   " + name);
    }
    catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e.GetType().Name + " " + e.Message); }
}

// ---- made-up field around "us" (from tools/onboard_preview)
var me0 = new Driver(9, "Angelo Ramorellos", "A. Ramorellos", "AR", "29", "BMW M Hybrid V8", "bmw", "GTP", "#000000",
    "A 4.99", "0x0153DB", 2, 2, 7, 0.5f, 6, 0, 214.1f, 215.0f, 1.2, 1.18, false, true, false, false, 2500);
var me = me0 with { LapDistPct = 0.214f };
var ahead = me with { CarIdx = 3, Name = "Kevin Estre", AbbrevName = "K. Estre", CarNumber = "6", CarName = "Porsche 963", CarBrand = "porsche", PositionInClass = 1, IntervalSeconds = null, LapDistPct = 0.2165f };
var behind = me with { CarIdx = 5, Name = "Jake Haydu", AbbrevName = "J. Haydu", CarNumber = "50", CarName = "Cadillac V-Series.R", CarBrand = "cadillac", PositionInClass = 3, IntervalSeconds = 0.62, LapDistPct = 0.2118f };
var snap0 = RaceSnapshot.Disconnected with { IsConnected = true, Phase = RacePhase.Broadcast, SessionTypeName = "Race", CapturedAt = DateTime.UtcNow,
    ClassLeaderboard = new List<DriverClass> { new("GTP", "#000000", new List<Driver> { ahead, me, behind }) }, PlayerCarIdx = 9, Player = me,
    TrackDisplayName = "Circuit des 24 Heures du Mans", TrackRawName = "lemans full", TrackBroadcastName = "Le Mans", TrackLatitude = 47.9498, TrackLongitude = 0.2077,
    Extras = RaceSnapshot.Disconnected.Extras with { TrackLength = "13.5541 km" } };
var ob0 = new OnboardData(9, 11, "CamRoll Bar", true, true, true, 0.0f, 0.0f, 0f, 0f, 4.7f, 70f, 5, 8000, 9500, 10000, true);

// 1. the normal broadcast: tower + session bar + watch + team radio
Scene("01_tower_bar_radio", c =>
{
    var comp = new Comp { RadioAtMs = 0, RadioWho = "ENGINEER" };
    using (var nd = new SKNoDrawCanvas(W, H)) for (double t = 0; t <= 2600; t += 50) { comp.Under(nd, t); comp.Top(nd, t); }
    comp.Under(c, 2650); comp.Top(c, 2650);
});

// 2. a pit stops page on the tower (two-line pit values) + race control on the radio card
Scene("02_tower_pitboard", c =>
{
    var stops = (System.Collections.IList)typeof(RaceDiary).GetField("stops", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
    var comp = new Comp { RadioAtMs = 0, RadioWho = "RACE CONTROL", RadioText = "Car 31 - track limits warning, turn 5." };
    var gtp = comp.Snap.ClassLeaderboard[0].Drivers;
    for (int i = 0; i < gtp.Count; i += 2) stops.Add(new RaceDiary.PitStop(gtp[i].CarIdx, gtp[i].Name, gtp[i].CarBrand, "GTP", 7 + i, 31.2 + i));
    var sdt = typeof(SceneDirector);
    var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
    var oldScenes = sdt.GetField("activeScenes", bf)!.GetValue(null);
    sdt.GetField("activeScenes", bf)!.SetValue(null, new List<Scene> { new Scene(SceneFocus.Pits, "GTP", comp.Snap.ClassLeaderboard[0].Color) });
    sdt.GetField("currentSceneIdx", bf)!.SetValue(null, 0);
    using (var nd = new SKNoDrawCanvas(W, H)) for (double t = 0; t <= 2600; t += 50) { comp.Under(nd, t); comp.Top(nd, t); }
    comp.Under(c, 2650); comp.Top(c, 2650);
    sdt.GetField("activeScenes", bf)!.SetValue(null, oldScenes);
});

// 3. battle mode: full-width bar + standings ticker + the battle card
Scene("03_battle_bar_ticker_card", c =>
{
    var comp = new Comp { Battle = true };
    var bc = new BattleCardRenderer();
    double t = 1000;
    for (int f = 0; f < 600; f++)
    {
        float pm = 0.40f + f * 0.0002f, u = 1f / 214f;
        Driver Mk(Driver b, int idx, string nm, string num, int pos, float off, double? iv) => b with { CarIdx = idx, Name = nm, AbbrevName = nm, CarNumber = num, PositionInClass = pos, LapDistPct = pm + off * u, CurrentLap = 8, LapsCompleted = 7, IntervalSeconds = iv, BestLapTime = 214f, LapDifference = 0, IsInPit = false };
        var lead = Mk(ahead, 3, "K. Estre", "6", 2, 0.8f, 1.2);
        var mine = Mk(me, 9, "A. Ramorellos", "29", 3, 0f, 0.8);
        var back = Mk(behind, 5, "J. Haydu", "50", 4, -0.5f, 0.5);
        var sn = snap0 with { ClassLeaderboard = new List<DriverClass> { new("GTP", "#000000", new List<Driver> { lead, mine, back }) }, Player = mine };
        float wl = (W - 1280f) / 2f, wt = H - 348f;
        if (f == 599) { comp.Under(c, t - 1000); bc.Draw(c, wl + 140f, wt + 30f, sn, t); comp.Top(c, t - 1000); }
        else { using var nd = new SKNoDrawCanvas(W, H); bc.Draw(nd, wl + 140f, wt + 30f, sn, t); comp.Under(nd, t - 1000); comp.Top(nd, t - 1000); }
        t += 16.7;
    }
});

// 4. the full-screen onboard (tach, KM/H, RPM, G, sectors, the cars either side)
Scene("04_onboard_full", c =>
{
    var r = new OnboardFullRenderer();
    double t = 1000; const int lapFrames = 900;
    for (int lap = 1; lap <= 2; lap++)
        for (int f = 0; f < (lap == 1 ? lapFrames : (int)(lapFrames * 0.6f)); f++)
        {
            float p = f / (float)lapFrames;
            float brk = p > 0.5f && p < 0.56f ? 0.8f : 0f, thr = brk > 0 ? 0f : 0.9f;
            var ob = ob0 with { Throttle = thr, Brake = brk, SteerRad = 0.3f, Rpm = 7200 + 1500 * thr, SpeedMs = 60 + 20 * thr, Gear = thr > 0.5f ? 6 : 3, LapPct = p,
                HasG = true, HasAbsTc = true, LatAccel = 6f, LongAccel = -10f * brk + 3f * thr, DeltaBest = lap == 2 ? 0.21f : 0f, DeltaOk = lap == 2 };
            var drv = me with { LapDistPct = p, LastLapTime = lap == 2 ? 213.9f : 0, BestLapTime = lap == 2 ? 213.9f : 0 };
            var cl = new List<DriverClass> { new("GTP", "#000000", new List<Driver> { ahead with { LapDistPct = (p + 0.004f) % 1 }, drv, behind with { LapDistPct = (p + 0.997f) % 1 } }) };
            var sn = snap0 with { Onboard = ob, PlayerOnboard = ob, ClassLeaderboard = cl, Player = drv };
            r.Feed(sn, t);
            bool last = lap == 2 && f == (int)(lapFrames * 0.6f) - 1;
            if (last) r.Draw(c, W, H, sn, t, 1f); else { using var nd = new SKNoDrawCanvas(W, H); r.Draw(nd, W, H, sn, t, 1f); }
            t += 16.7;
        }
});

// 5. the small onboard card + pit battle + battle-of-the-pack card
Scene("05_onboard_card_pitbattle_pack", c =>
{
    var ob = ob0 with { Throttle = 0.8f, Brake = 0.2f, Rpm = 8400, SpeedMs = 72, Gear = 5 };
    var sn = snap0 with { Onboard = ob, PlayerOnboard = ob };
    var oc = new OnboardRenderer();
    for (double t = 0; t <= 1500; t += 33) { using var nd = new SKNoDrawCanvas(W, H); oc.Draw(nd, 40f, 520f, sn, t, 1f); }
    oc.Draw(c, 40f, 520f, sn, 1533, 1f);
    var pb = new PitBattleCard();
    var mineP = me with { IsInPit = true, LapDistPct = 0.98f };
    var rivalP = ahead with { IsInPit = true, LapDistPct = 0.985f };
    for (double t = 0; t <= 1500; t += 33) { using var nd = new SKNoDrawCanvas(W, H); pb.Draw(nd, W / 2f + 300, 520f, sn, mineP, rivalP, 2, t, 1f); }
    pb.Draw(c, W / 2f + 300, 520f, sn, mineP, rivalP, 2, 1533, 1f);
    var pack = new PackCardRenderer();
    var many = new List<Driver> { ahead with { PositionInClass = 4 }, me with { PositionInClass = 5, IntervalSeconds = 0.4 }, behind with { PositionInClass = 6, IntervalSeconds = 0.7 },
                                  behind with { CarIdx = 7, Name = "Nick Tandy", AbbrevName = "N. Tandy", CarNumber = "4", PositionInClass = 7, IntervalSeconds = 0.9 } };
    var sp = sn with { ClassLeaderboard = new List<DriverClass> { new("GTP", "#000000", many) }, CarSteer = new float[64], CarGear = new int[64] };
    try
    {
        for (double t = 0; t <= 1500; t += 33) { using var nd = new SKNoDrawCanvas(W, H); pack.Draw(nd, W / 2f, H - 30f, sp, t); }
        pack.Draw(c, W / 2f, H - 30f, sp, 1533);
    }
    catch (Exception e) { Console.WriteLine("  (pack card skipped: " + e.Message + ")"); }
});

// 6. corner labels: the big one (with its heritage line) and the compact one
Scene("06_corner_labels", c =>
{
    CornerTheme.DrawLabel(c, W / 2f, 120f, "INTO", "TERTRE ROUGE", 1f, CornerTheme.HeritageOf("lemans full", "TERTRE ROUGE"));
    CornerTheme.DrawLabel(c, W / 2f, 420f, "THROUGH", "PORSCHE CURVES", 1f, CornerTheme.HeritageOf("lemans full", "PORSCHE CURVES"));
    CornerTheme.DrawCompact(c, 80f, 760f, 420f, "INTO", "ARNAGE", 1f);
    CornerTheme.DrawCompact(c, 80f, 860f, 420f, "INTO", "INDIANAPOLIS", 1f);
});

// 7. fastest lap + pit callout + featured driver card
Scene("07_fastest_pit_featured", c =>
{
    var comp = new Comp { Featured = true, Tower = false };
    var fl = new FastestLapRenderer();
    var pc = new PitCalloutRenderer();
    var baseSnap = comp.Snap;
    var gtp = baseSnap.ClassLeaderboard[0].Drivers.ToList();
    var faster = baseSnap with { ClassLeaderboard = new[] { new DriverClass("GTP", baseSnap.ClassLeaderboard[0].Color, gtp.Select((d, i) => i == 2 ? d with { BestLapTime = 93.4f } : d).ToList()) }.Concat(baseSnap.ClassLeaderboard.Skip(1)).ToList() };
    var pitted = faster with { ClassLeaderboard = new[] { new DriverClass("GTP", baseSnap.ClassLeaderboard[0].Color, faster.ClassLeaderboard[0].Drivers.Select((d, i) => i == 4 ? d with { IsInPit = true } : d).ToList()) }.Concat(faster.ClassLeaderboard.Skip(1)).ToList() };
    using var nd = new SKNoDrawCanvas(W, H);
    double t = 0;
    for (; t < 400; t += 33) { fl.Draw(nd, 0, 0, baseSnap, t); pc.Draw(nd, 0, 0, baseSnap, t); comp.Under(nd, t); }
    for (; t < 2200; t += 33) { fl.Draw(nd, 0, 0, pitted, t); pc.Draw(nd, 0, 0, pitted, t); comp.Under(nd, t); }
    float fx = (W - FastestLapRenderer.CardWidth) / 2f, px = (W - PitCalloutRenderer.CardWidth) / 2f;
    comp.Under(c, t);
    fl.Draw(c, fx, 160f, pitted, t);
    pc.Draw(c, px, 360f, pitted, t);
});

// 8. the battle map (fight between cars on the track outline)
Scene("08_battle_map", c =>
{
    var map = new BattleMap();
    var cars = new List<int> { 3, 9, 5 };
    for (double t = 0; t <= 2000; t += 33) { using var nd = new SKNoDrawCanvas(W, H); map.Draw(nd, W - 24f - BattleMap.W, 172f, snap0, cars, true, t); }
    map.Draw(c, W - 24f - BattleMap.W, 172f, snap0, cars, true, 2033);
});

// managed bytes per frame (warm), so a before / after build can be compared: the comp (tower + bar + radio) and the
// full-screen onboard, on a no-draw canvas
{
    using var nd = new SKNoDrawCanvas(W, H);
    var comp = new Comp { RadioAtMs = 0 };
    for (int i = 0; i < 300; i++) { comp.Under(nd, 3000 + i * 16.7); comp.Top(nd, 3000 + i * 16.7); }
    long a0 = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 600; i++) { comp.Under(nd, 8000 + i * 16.7); comp.Top(nd, 8000 + i * 16.7); }
    Console.WriteLine($"alloc comp (tower+bar+watch+radio): {(GC.GetAllocatedBytesForCurrentThread() - a0) / 600.0:0} B/frame");
    var r = new OnboardFullRenderer();
    var ob = ob0 with { Throttle = 0.7f, Rpm = 8000, SpeedMs = 70, Gear = 5, HasG = true, HasAbsTc = true, DeltaOk = true, DeltaBest = 0.1f };
    var sn = snap0 with { Onboard = ob, PlayerOnboard = ob };
    for (int i = 0; i < 300; i++) { r.Feed(sn, i * 16.7); r.Draw(nd, W, H, sn, i * 16.7, 1f); }
    a0 = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 300; i < 900; i++) { r.Feed(sn, i * 16.7); r.Draw(nd, W, H, sn, i * 16.7, 1f); }
    Console.WriteLine($"alloc onboard full: {(GC.GetAllocatedBytesForCurrentThread() - a0) / 600.0:0} B/frame");
}
Console.WriteLine("done");
