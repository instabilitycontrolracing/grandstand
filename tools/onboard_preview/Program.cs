// Onboard preview (2026-10-24): the small card and the full-screen treatment, rendered with sample data.
// Usage: dotnet onboard_preview.dll OUT_DIR [background.png]
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

if (args.Length > 0 && args[0] == "livery")
{
    // 2026-10-25: livery band test - args: livery OUT_DIR carpath:userid ...
    ApexHUD.Native.Overlays.OverlayLog.Sink = Console.WriteLine;
    foreach (var spec in args.Skip(2))
    {
        var parts = spec.Split(':');
        var dv = new Driver(1, "Test", "T", "T", "1", "car", "x", "GT3", "#ffffff", "A", "#000", 1, 1, 1, 0.1f, 1, 0, 0, 0, null, null, false, true, false, false, 1000,
                            CarPath: parts[0], UserId: int.Parse(parts[1]));
        SKImage? im = null;
        for (int i = 0; i < 200 && (im = LiveryArt.Get(dv)) == null; i++) Thread.Sleep(100);
        if (im == null) { Console.WriteLine("no image for " + spec); continue; }
        using var data = im.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(Path.Combine(args[1], "lv_" + parts[0] + ".png"), data.ToArray());
        Console.WriteLine("saved " + parts[0] + " " + im.Width + "x" + im.Height);
    }
    return;
}
if (args.Length > 0 && args[0] == "pit")
{
    var stops = (System.Collections.IList)typeof(RaceDiary).GetField("stops", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
    var rnd = new Random(3);
    string[] nm = { "K. Estre", "A. Ramorellos", "J. Haydu", "N. Yelloly", "R. van der Linde", "S. Bourdais" };
    for (int i = 0; i < 6; i++) stops.Add(new RaceDiary.PitStop(i == 1 ? 9 : 20 + i, nm[i], "bmw", "GTP", 7 + i, 31.2 + rnd.NextDouble() * 9));
    var mp = new Driver(9, "Angelo Ramorellos", "A. Ramorellos", "AR", "29", "BMW M Hybrid V8", "bmw", "GTP", "#000000", "A 4.99", "0x0153DB", 2, 2, 7, 0.5f, 6, 0, 214.1f, 215.0f, 1.2, 1.18, false, true, false, false, 2500);
    var sn = RaceSnapshot.Disconnected with { IsConnected = true, Phase = RacePhase.Broadcast, SessionTypeName = "Race", CapturedAt = DateTime.UtcNow, Player = mp, PlayerCarIdx = 9,
                ClassLeaderboard = new List<DriverClass> { new("GTP", "#000000", new List<Driver> { mp }) } };
    using var surf = SKSurface.Create(new SKImageInfo(1920, 1080));
    var c = surf.Canvas;
    var bgp = args.Length > 2 ? SKBitmap.Decode(args[2]) : null;
    if (bgp != null) { using var bp = new SKPaint { FilterQuality = SKFilterQuality.High }; c.DrawBitmap(bgp, new SKRect(0, 0, 1920, 1080), bp); } else c.Clear(new SKColor(60, 66, 74));
    StoryDirector.Force(StoryKind.PitStops, 0);
    new StoryCards().Draw(c, 1920, 1080, sn, 5000);
    using var img = surf.Snapshot(); File.WriteAllBytes(Path.Combine(args[1], "pittower.png"), img.Encode(SKEncodedImageFormat.Png, 92).ToArray());
    return;
}
if (args.Length > 0 && args[0] == "yellowbar")
{
    // 2026-10-25: the session bar as a flashing LED light box under a yellow. args: yellowbar OUT background.png
    typeof(YellowFlagCallout).GetProperty("CurrentPlace")!.SetValue(null, "PORSCHE CURVES");
    var sn = RaceSnapshot.Disconnected with { IsConnected = true, Phase = RacePhase.Broadcast, SessionTypeName = "Race", TrackDisplayName = "Circuit des 24 Heures du Mans", TrackRawName = "lemans full", TrackCountryCode = "FR",
        Flags = RaceSnapshot.Disconnected.Flags with { Yellow = true }, Extras = RaceSnapshot.Disconnected.Extras with { TrackLength = "13.5541 km" } };
    var bgp = SKBitmap.Decode(args[2]);
    foreach (var (wd, tms, nm) in new[] { (976f, 100.0, "a"), (976f, 600.0, "b"), (1920f, 100.0, "c") })
    {
        var r = new SessionBarRenderer();
        using var surf = SKSurface.Create(new SKImageInfo(1920, 160)); var c = surf.Canvas;
        using (var bp = new SKPaint { FilterQuality = SKFilterQuality.Medium }) c.DrawBitmap(bgp, new SKRect(0, 0, bgp.Width, bgp.Width * 160f / 1920f), new SKRect(0, 0, 1920, 160), bp);
        for (double t = 0; t <= tms + 400; t += 50) r.Draw(c, (1920 - wd) / 2f, 18, 1f, sn, DateTime.Now, 1000 + t, wd);
        c.Clear(); c.DrawBitmap(bgp, new SKRect(0, 0, bgp.Width, bgp.Width * 160f / 1920f), new SKRect(0, 0, 1920, 160));
        r.Draw(c, (1920 - wd) / 2f, 18, 1f, sn, DateTime.Now, 1000 + tms + 450, wd);
        using var img = surf.Snapshot(); File.WriteAllBytes(Path.Combine(args[1], $"ybar_{nm}.png"), img.Encode(SKEncodedImageFormat.Png, 92).ToArray());
    }
    return;
}
if (args.Length > 0 && args[0] == "trackat")
{
    // full-size track card frames at chosen seconds. args: trackat OUT background.png t1,t2,...
    var sn = RaceSnapshot.Disconnected with { IsConnected = true, Phase = RacePhase.PreRace, TrackDisplayName = "Circuit des 24 Heures du Mans", TrackRawName = "lemans full", TrackConfigName = "24 Heures du Mans", TrackBroadcastName = "Circuit des 24 Heures du Mans", TrackCountryCode = "FR", Extras = RaceSnapshot.Disconnected.Extras with { TrackLength = "13.5541 km", TrackCity = "Le Mans", TrackCountry = "France", TrackTurns = 38 } };
    var bgp = SKBitmap.Decode(args[2]);
    StartingGridRenderer.CardsStartMs = 0;
    sn = sn with { TrackLatitude = 47.9563, TrackLongitude = 0.2075 };
    var pc = new PreRaceCards { Screen = new SKRect(0, 0, 1920, 1080) };
    using var bmp = new SKBitmap(1920, 1080); using var c = new SKCanvas(bmp);
    {
        var mb = TrackMapCache.Resolve(sn.TrackConfigName, sn.TrackDisplayName, sn.TrackBroadcastName, sn.TrackRawName);
        Console.WriteLine("map " + mb);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (mb != null && TrackTerrain.For(mb, Assets.Resolve("maps", mb + ".json"), sn.TrackLatitude, sn.TrackLongitude, TrackCorners.LengthM(sn.Extras.TrackLength)) == null && sw.Elapsed.TotalSeconds < 240) Thread.Sleep(500);
        Console.WriteLine("terrain wait " + sw.Elapsed.TotalSeconds);
    }
    foreach (var ts in args[3].Split(','))
    {
        double tt = double.Parse(ts, System.Globalization.CultureInfo.InvariantCulture);
        for (double t = Math.Max(0, tt - 2); t <= tt + 1e-6; t += 0.1)
        {
            using (var bp = new SKPaint { FilterQuality = SKFilterQuality.Medium }) c.DrawBitmap(bgp, new SKRect(0, 0, 1920, 1080), bp);
            pc.Draw(c, 1920 - 1000, 860, sn, (t + 0.6) * 1000);
        }
        using var d = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 85);
        File.WriteAllBytes(Path.Combine(args[1], $"ta_{ts}.png"), d.ToArray());
    }
    return;
}
if (args.Length > 0 && args[0] == "trackanim")
{
    // 2026-10-25: the whole track card as a film (frames at 10 fps, half size). args: trackanim OUT background.png
    var sn = RaceSnapshot.Disconnected with { IsConnected = true, Phase = RacePhase.PreRace, TrackDisplayName = "Circuit des 24 Heures du Mans", TrackRawName = "lemans full", TrackConfigName = "24 Heures du Mans", TrackBroadcastName = "Circuit des 24 Heures du Mans", TrackCountryCode = "FR", Extras = RaceSnapshot.Disconnected.Extras with { TrackLength = "13.5541 km", TrackCity = "Le Mans", TrackCountry = "France", TrackTurns = 38 } };
    var bgp = SKBitmap.Decode(args[2]);
    StartingGridRenderer.CardsStartMs = 0;
    var pc = new PreRaceCards { Screen = new SKRect(0, 0, 1920, 1080) };
    using var bmp = new SKBitmap(960, 540); using var c = new SKCanvas(bmp);
    int fno = 0;
    for (double t = 0; t <= 50; t += 0.1)
    {
        using (var bp = new SKPaint { FilterQuality = SKFilterQuality.Medium }) c.DrawBitmap(bgp, new SKRect(0, 0, 960, 540), bp);
        c.Save(); c.Scale(0.5f);
        pc.Draw(c, 1920 - 1000, 860, sn, (t + 0.6) * 1000);
        c.Restore();
        using var d = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 80);
        File.WriteAllBytes(Path.Combine(args[1], $"f{fno++:0000}.png"), d.ToArray());
    }
    return;
}
if (args.Length > 0 && args[0] == "themes")
{
    // 2026-10-25: every themed corner label as it shows on the onboard (top centre). args: themes OUT [background.png]
    string[] names = { "INTO|Dunlop Curve", "THROUGH|Dunlop Chicane", "INTO|The Esses", "INTO|Tetre Rouge", "DOWN|The First Mulsanne Straight", "INTO|Playstation Chicane",
                       "INTO|Michelin Chicane", "INTO|Mulsanne Corner", "INTO|Indianapolis", "INTO|Arnage", "THROUGH|Porsche Curves", "INTO|Corvette Corner", "INTO|The First Ford Chicane" };
    var bgp = args.Length > 2 ? SKBitmap.Decode(args[2]) : null;
    foreach (var item in names)
    {
        var parts = item.Split('|');
        using var bmp = new SKBitmap(1200, 330); using var c = new SKCanvas(bmp);
        if (bgp != null) { using var bp = new SKPaint { FilterQuality = SKFilterQuality.High }; c.DrawBitmap(bgp, new SKRect(0, 0, bgp.Width, bgp.Width * 330f / 1200f), new SKRect(0, 0, 1200, 330), bp); }
        else c.Clear(new SKColor(52, 58, 66));
        CornerTheme.DrawLabel(c, 600, 74, parts[0], parts[1].ToUpperInvariant(), 1f, CornerTheme.HeritageOf("lemans full", parts[1]));
        using var d = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 92);
        File.WriteAllBytes(Path.Combine(args[1], "theme_" + parts[1].Replace(' ', '_') + ".png"), d.ToArray());
    }
    return;
}
if (args.Length > 0 && args[0] == "lapwalk")
{
    foreach (var st in TrackCorners.Lap("lemans full", "13.5541 km") ?? new()) Console.WriteLine($"{st.Name} {st.StartPct:0.000}-{st.EndPct:0.000} {(st.Straight ? "S" : "C")}");
    var sn = RaceSnapshot.Disconnected with { IsConnected = true, Phase = RacePhase.PreRace, TrackDisplayName = "Circuit des 24 Heures du Mans", TrackRawName = "lemans full", TrackConfigName = "24 Heures du Mans", TrackBroadcastName = "Circuit des 24 Heures du Mans", Extras = RaceSnapshot.Disconnected.Extras with { TrackLength = "13.5541 km", TrackCity = "Le Mans", TrackCountry = "France" } };
    StartingGridRenderer.CardsStartMs = 0;
    foreach (bool panel in new[] { false, true })
        foreach (double ts in new[] { 1.5, 4.0, 9.0, 20.0 })
        {
            var pc = new PreRaceCards();
            if (panel) pc.Screen = new SKRect(0, 0, 1920, 1080);
            using var bmp = new SKBitmap(1920, 1080); using var c = new SKCanvas(bmp);
            c.Clear(new SKColor(70, 80, 90));
            for (double t = 0; t <= ts; t += 0.25) { c.Clear(new SKColor(70, 80, 90)); pc.Draw(c, 1920 - 1000, 860, sn, (t + 0.6) * 1000); }
            using var d = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 90);
            File.WriteAllBytes(Path.Combine(args[1], $"lw_{(panel ? "p" : "c")}_{ts}.png"), d.ToArray());
        }
    return;
}
if (args.Length > 0 && args[0] == "cars")
{
    // 2026-10-29 (car parity): the onboard treatment for different cars, driven by CarCatalog. Each car is drawn as our
    // own car (pedals + TC / LOCK pods) with the catalog's shift rpm for the gear it's in (what Grandstand uses when
    // iRacing sends none, and always for another model on camera). TC lights only when a source says the car has TC
    // (live, iRacing's dcTractionControl decides too - not available here). args: cars OUT [background.png] [carId,...]
    string od = args[1];
    var bgc = args.Length > 2 && File.Exists(args[2]) ? SKBitmap.Decode(args[2]) : null;
    var ids = args.Length > 3 ? args[3].Split(',').Select(int.Parse).ToArray() : new[] { 159, 174, 173, 189, 147, 158 };
    foreach (var id in ids)
    {
        var info = CarCatalog.Find(id, null)!;
        const int W = 1920, H = 1080;
        using var surf = SKSurface.Create(new SKImageInfo(W, H));
        var c = surf.Canvas;
        if (bgc != null)
        {
            var src = new SKRect(0, 0, bgc.Width, bgc.Height * 0.45f);
            float sc = Math.Max(W / src.Width, H / src.Height);
            using var bp = new SKPaint { FilterQuality = SKFilterQuality.High };
            c.DrawBitmap(bgc, src, new SKRect((W - src.Width * sc) / 2, (H - src.Height * sc) / 2, (W + src.Width * sc) / 2, (H + src.Height * sc) / 2), bp);
        }
        else
        {
            using var bp = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, H), new[] { new SKColor(110, 120, 130), new SKColor(40, 42, 46) }, null, SKShaderTileMode.Clamp) };
            c.DrawRect(0, 0, W, H, bp);
        }
        var drv = new Driver(9, "Angelo Ramorellos", "A. Ramorellos", "AR", "29", info.Name, info.LogoSlug ?? info.Maker ?? "", info.ClassLabel,
            BroadcastPalette.Class(info.ClassLabel), "A 4.99", "0x0153DB", 2, 2, 7, 0.5f, 6, 0, 0, 0, 1.2, 1.18, false, true, false, false, 2500)
            { CarPath = info.CarPath ?? "", CarId = info.CarId };
        var rival = drv with { CarIdx = 3, Name = "Kevin Estre", AbbrevName = "K. Estre", CarNumber = "6", PositionInClass = 1, IntervalSeconds = null };
        var cl = new List<DriverClass> { new(info.ClassLabel, BroadcastPalette.Class(info.ClassLabel), new List<Driver> { rival, drv }) };
        int gear = 3;
        float shiftRpm = info.ShiftFor(gear);
        bool tcCar = info.Tc == true;
        var r = new OnboardFullRenderer();
        double t = 1000;
        for (int f = 0; f < 90; f++)
        {
            float rpm = shiftRpm > 0 ? shiftRpm * 0.985f : 7200f;
            var ob = new OnboardData(9, 11, "CamRoll Bar", true, true, true, 0.82f, 0f, 0f, -0.12f, 4.7f, 52f, gear, rpm, shiftRpm, 0f, false,
                LapPct: 0.30f, HasG: true, LatAccel: 6f, LongAccel: 3f, AbsActive: false, TcActive: tcCar, HasAbsTc: true, CarHasAbs: false, CarHasTc: tcCar);
            var sn = RaceSnapshot.Disconnected with { IsConnected = true, Phase = RacePhase.Broadcast, ClassLeaderboard = cl, PlayerCarIdx = 9, Player = drv,
                TrackDisplayName = "Circuit des 24 Heures du Mans", TrackRawName = "lemans full", Onboard = ob, PlayerOnboard = ob,
                Extras = RaceSnapshot.Disconnected.Extras with { TrackLength = "13.5541 km" } };
            r.Feed(sn, t);
            if (f == 89) r.Draw(c, W, H, sn, t, 1f); else { using var nd = new SKNoDrawCanvas(W, H); r.Draw(nd, W, H, sn, t, 1f); }
            t += 16.7;
        }
        // caption: which car, what the data says (preview only - not part of the overlay)
        using (var cp = new SKPaint { IsAntialias = true, Color = SKColors.White, TextSize = 22, Typeface = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Bold) })
        using (var bk = new SKPaint { Color = new SKColor(0, 0, 0, 170) })
        {
            string cap1 = $"PREVIEW  {info.Name}  |  CarID {info.CarId}  |  CarPath {info.CarPath ?? "unverified"}  |  class {info.ClassLabel}  |  maker {(info.LogoSlug ?? "(text) " + (info.Maker ?? "-"))}";
            string cap = $"shift {(shiftRpm > 0 ? shiftRpm.ToString("0") + " rpm (gear " + gear + ", Lovely)" : "not known - no lights")}  |  TC {(info.Tc == true ? "yes (sourced)" : info.Tc == false ? "no" : "unverified - off here, live dial decides")}  |  " +
                         $"hybrid {(info.Hybrid == true ? "yes" : info.Hybrid == false ? "no" : info.Electric ? "electric" : "unverified")}  |  gears {(info.Gears > 0 ? info.Gears.ToString() : "unverified")}";
            cp.TextSize = 18;
            c.DrawRect(0, H - 58, W, 58, bk);
            c.DrawText(cap1, 16, H - 34, cp);
            c.DrawText(cap, 16, H - 10, cp);
        }
        using var img = surf.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 92);
        var fn = "onboard_" + (info.CarPath ?? "carid" + info.CarId).Replace(' ', '_') + ".png";
        File.WriteAllBytes(Path.Combine(od, fn), data.ToArray());
        Console.WriteLine(fn);
    }
    return;
}
string outDir = args.Length > 0 ? args[0] : ".";
string? bgPath = args.Length > 1 ? args[1] : null;
var me0 = new Driver(9, "Angelo Ramorellos", "A. Ramorellos", "AR", "29", "BMW M Hybrid V8", "bmw", "GTP", "#000000",
    "A 4.99", "0x0153DB", 2, 2, 7, 0.5f, 6, 0, 214.1f, 215.0f, 1.2, 1.18, false, true, false, false, 2500);
var me = me0 with { CarPath = "bmwlmdh", UserId = 100, LapDistPct = 0.214f };
var ahead = me with { CarIdx = 3, Name = "Kevin Estre", AbbrevName = "K. Estre", CarNumber = "6", PositionInClass = 1, IntervalSeconds = null, CarPath = "mercedesamgevogt3", LapDistPct = 0.2165f };
var behind = me with { CarIdx = 5, Name = "Jake Haydu", AbbrevName = "J. Haydu", CarNumber = "50", PositionInClass = 3, IntervalSeconds = 0.62, CarPath = "fordmustanggt3", LapDistPct = 0.2118f };
// 2026-10-25: car pictures from iRacing's renderer, saved from the PC (GS_CARS = folder with me.png / ahead.png / behind.png)
var carsDir = Environment.GetEnvironmentVariable("GS_CARS");
if (carsDir != null)
{
    LiveryArt.Inject(me, File.ReadAllBytes(Path.Combine(carsDir, "me.png")));
    LiveryArt.Inject(ahead, File.ReadAllBytes(Path.Combine(carsDir, "ahead.png")));
    LiveryArt.Inject(behind, File.ReadAllBytes(Path.Combine(carsDir, "behind.png")));
}
var classes = new List<DriverClass> { new("GTP", "#000000", new List<Driver> { ahead, me, behind }) };
var snap0 = RaceSnapshot.Disconnected with { IsConnected = true, Phase = RacePhase.Broadcast, ClassLeaderboard = classes, PlayerCarIdx = 9, TrackDisplayName = "Circuit des 24 Heures du Mans", TrackRawName = "lemans full", TrackLatitude = 47.9498, TrackLongitude = 0.2077, Extras = RaceSnapshot.Disconnected.Extras with { TrackLength = "13.5541 km" } };
{   // the 3D maps' ground (cached from an earlier build)
    var mb0 = TrackMapCache.Resolve(snap0.TrackConfigName, snap0.TrackDisplayName, snap0.TrackBroadcastName, snap0.TrackRawName);
    var sw0 = System.Diagnostics.Stopwatch.StartNew();
    while (mb0 != null && TrackTerrain.For(mb0, Assets.Resolve("maps", mb0 + ".json"), snap0.TrackLatitude, snap0.TrackLongitude, 13554) == null && sw0.Elapsed.TotalSeconds < 120) Thread.Sleep(200);
    Thread.Sleep(1600);   // past the fade-in
}
var ob0 = new OnboardData(9, 11, "CamRoll Bar", true, true, true, 0.0f, 0.0f, 0f, 0f, 4.7f, 70f, 5, 8000, 9500, 10000, true);

SKBitmap? bg = bgPath != null && File.Exists(bgPath) ? SKBitmap.Decode(bgPath) : null;
void Background(SKCanvas c, int W, int H)
{
    if (bg != null)
    {
        // cover-fit the clean upper part of the screenshot (the cockpit, above the old cards)
        var src = new SKRect(0, 0, bg.Width, bg.Height * 0.45f);
        float s = Math.Max(W / src.Width, H / src.Height);
        float w = src.Width * s, h = src.Height * s;
        using var p = new SKPaint { FilterQuality = SKFilterQuality.High };
        c.DrawBitmap(bg, src, new SKRect((W - w) / 2, (H - h) / 2, (W + w) / 2, (H + h) / 2), p);
    }
    else
    {
        using var p = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, H), new[] { new SKColor(110, 120, 130), new SKColor(40, 42, 46) }, null, SKShaderTileMode.Clamp) };
        c.DrawRect(0, 0, W, H, p);
    }
}

OnboardData At(int f, int n, OnboardData b)
{
    // a braking zone into a right-hander, then back on the power: 8 s of history
    float u = f / (float)n;
    float brk = u is > 0.30f and < 0.52f ? Math.Clamp(1 - Math.Abs(u - 0.36f) * 6f, 0, 1) * 0.92f : 0f;
    float thr = u < 0.28f ? 1f : u < 0.55f ? 0f : Math.Clamp((u - 0.55f) * 3.2f, 0, 1);
    float steer = u is > 0.40f and < 0.75f ? -0.9f * MathF.Sin((u - 0.40f) / 0.35f * MathF.PI) : 0f;
    float rpm = 6200 + 3100 * thr * (0.6f + 0.4f * u);
    return b with { Throttle = thr, Brake = brk, SteerRad = steer, Rpm = Math.Min(rpm, 9420), SpeedMs = 38 + 46 * (u < 0.3f ? 1 : u < 0.55f ? 0.4f : u), Gear = u < 0.3f ? 6 : u < 0.6f ? 3 : 5 };
}

int GearOverride = args.Length > 2 ? int.Parse(args[2]) : 0;
// full screen: lap 1 = the best lap, then lap 2 brakes earlier and longer into the same corner (losing time)
{
    const int W = 1920, H = 1080;
    using var surf = SKSurface.Create(new SKImageInfo(W, H));
    var c = surf.Canvas;
    Background(c, W, H);
    var r = new OnboardFullRenderer();
    double t = 1000; int lapFrames = 1400;
    (float thr, float brk, float steer, float rpm, float spd, int gear) Pedals(float p, bool slow)
    {
        // one big braking zone at 62-66 % of the lap (lap 2 brakes at 60 % and stays on it longer)
        float bs = slow ? 0.600f : 0.620f, be = slow ? 0.668f : 0.655f;
        float brk = p > bs && p < be ? Math.Clamp(1f - (p - bs) / (be - bs) * 0.6f, 0, 1) * 0.92f : 0f;
        float thr = p < bs ? 1f : p < be + 0.01f ? 0f : Math.Clamp((p - be - 0.01f) * 22f, 0, 1);
        float steer = p > be - 0.01f && p < be + 0.06f ? -0.9f * MathF.Sin((p - be + 0.01f) / 0.07f * MathF.PI) : 0f;
        return (thr, brk, steer, 6400 + 3000 * thr, 40 + 44 * thr, GearOverride > 0 ? GearOverride : (thr > 0.5f ? 6 : 3));
    }
    void Feed(int lap, int from, int to, Driver drv, bool draw)
    {
        for (int f = from; f < to; f++)
        {
            float p = f / (float)lapFrames;
            var (thr, brk, steer, rpm, spd, gear) = Pedals(p, lap == 2);
            float delta = lap == 2 ? Math.Clamp((p - 0.60f) * 6f, 0, 0.32f) - Math.Clamp((p - 0.74f) * 1.5f, 0, 0.12f) : 0f;
            var ob = ob0 with { Throttle = thr, Brake = brk, SteerRad = steer, Rpm = rpm, SpeedMs = spd, Gear = gear, LapPct = p, HasG = true, HasAbsTc = true, AbsActive = brk > 0.5f, TcActive = thr > 0.05f && thr < 0.95f, LatAccel = 9f * steer, LongAccel = -14f * brk + 4f * thr, DeltaBest = delta, DeltaOk = lap == 2 };
            var cl = new List<DriverClass> { new("GTP", "#000000", new List<Driver> { ahead with { LapDistPct = (p + 0.004f) % 1 }, drv with { LapDistPct = p }, behind with { LapDistPct = (p + 0.997f) % 1 } }) };
            var sn = snap0 with { Onboard = ob, PlayerOnboard = ob, ClassLeaderboard = cl, Player = drv };
            r.Feed(sn, t);
            if (draw && f == to - 1) r.Draw(c, W, H, sn, t, 1f);
            else { using var nd = new SKNoDrawCanvas(W, H); r.Draw(nd, W, H, sn, t, 1f); }
            t += 16.7;
        }
    }
    Feed(1, 0, lapFrames, me with { LastLapTime = 0, BestLapTime = 0 }, false);           // lap 1 (becomes the best)
    var done = me with { LastLapTime = 213.9f, BestLapTime = 213.9f };
    Feed(2, 0, 60, done, false);                                                          // over the line: lap time posted
    Feed(2, 60, (int)(lapFrames * 0.735f), done, true);                                   // lap 2, just past the corner
    using var img = surf.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 92);
    File.WriteAllBytes(Path.Combine(outDir, "onboard_fullscreen.png"), data.ToArray());
}
Console.WriteLine("ok");

// battle card preview (2026-10-25): defending, the car behind 0.5 s back
{
    const int W = 1920, H = 1080;
    using var surf = SKSurface.Create(new SKImageInfo(W, H));
    var c = surf.Canvas; Background(c, W, H);
    var bc = new BattleCardRenderer();
    double t = 1000;
    var strip = new List<SKImage>();
    for (int f = 0; f < 700; f++)
    {
        float pm = 0.40f + f * 0.0002f, u = 1f / 214f;
        Driver Mk(Driver b, int idx, string nm, string num, int pos, float off, double? iv) => b with { CarIdx = idx, Name = nm, AbbrevName = nm, CarNumber = num, PositionInClass = pos, LapDistPct = pm + off * u, CurrentLap = 8, LapsCompleted = 7, IntervalSeconds = iv, BestLapTime = 214f, LapDifference = 0, IsInPit = false };
        var a2 = Mk(ahead, 1, "M. Rockenfeller", "25", 1, 2.0f, null);
        var lead = Mk(ahead, 3, "K. Estre", "6", 2, 0.8f, 1.2);
        bool flip = f >= 600;
        var mine = Mk(me, 9, "A. Ramorellos", "29", 3, 0f, flip ? 0.4 : 0.8);
        var back = Mk(behind, 5, "J. Haydu", "50", 4, -0.5f, flip ? 1.5 : 0.5);
        var b2 = Mk(behind, 7, "N. Tandy", "4", 5, -1.5f, 1.0);
        var cl = new List<DriverClass> { new("GTP", "#000000", new List<Driver> { a2, lead, mine, back, b2 }) };
        var sn = snap0 with { ClassLeaderboard = cl, SessionTypeName = "Race", Player = mine };
        float wl = (W - 1280f) / 2f, wt = H - 348f;
        if (f == 599) bc.Draw(c, wl + 140f, wt + 30f, sn, t);
        else if (f >= 600 && (f - 600) % 6 == 0 && f < 690)
        {
            using var fs = SKSurface.Create(new SKImageInfo(W, 160)); var fc = fs.Canvas; fc.Clear(new SKColor(40, 44, 52));
            fc.Translate(0, -(H - 420)); bc.Draw(fc, wl + 140f, wt + 30f, sn, t); strip.Add(fs.Snapshot());
        }
        else { using var nd = new SKNoDrawCanvas(W, H); bc.Draw(nd, wl + 140f, wt + 30f, sn, t); }
        t += 16.7;
    }
    using (var ss = SKSurface.Create(new SKImageInfo(W, 160 * strip.Count)))
    {
        for (int i = 0; i < strip.Count; i++) ss.Canvas.DrawImage(strip[i], 0, 160 * i);
        using var si = ss.Snapshot(); File.WriteAllBytes(Path.Combine(outDir, "pressure_slide.png"), si.Encode(SKEncodedImageFormat.Png, 80).ToArray());
    }
    using var img = surf.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 92);
    File.WriteAllBytes(Path.Combine(outDir, "battle.png"), data.ToArray());
    Console.WriteLine("battle ok, active=" + bc.IsActive);
}

// contact card preview (2026-10-25): two cars, real car pictures
{
    const int W = 1920, H = 1080;
    using var surf = SKSurface.Create(new SKImageInfo(W, H));
    var c = surf.Canvas; Background(c, W, H);
    var list = (System.Collections.IList)typeof(RaceDiary).GetField("contacts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
    list.Add(new RaceDiary.Contact(9, "A. Ramorellos", "bmw", "GTP", 5, 4, 6, 5, "J. Haydu", "cadillac", 4, 8, DateTime.UtcNow));
    var sn = snap0 with { SessionTypeName = "Race", CapturedAt = DateTime.UtcNow, Player = me };
    StoryDirector.Force(StoryKind.CarContact, 0);
    new StoryCards().Draw(c, W, H, sn, 5000);
    using var img = surf.Snapshot(); File.WriteAllBytes(Path.Combine(outDir, "contact.png"), img.Encode(SKEncodedImageFormat.Png, 92).ToArray());
    // one car on its own
    c.Clear(); Background(c, W, H);
    list.Add(new RaceDiary.Contact(5, "J. Haydu", "cadillac", "GTP", 6, -1, 0, -1, null, null, 0, 0, DateTime.UtcNow));
    StoryDirector.Force(StoryKind.CarContact, 0);
    new StoryCards().Draw(c, W, H, sn, 5000);
    using var img2 = surf.Snapshot(); File.WriteAllBytes(Path.Combine(outDir, "contact_solo.png"), img2.Encode(SKEncodedImageFormat.Png, 92).ToArray());
    // a disqualification (2026-10-25)
    c.Clear(); Background(c, W, H);
    list.Add(new RaceDiary.Contact(5, "J. HAYDU", "cadillac", "GTP", 14, -1, 25, -1, null, null, 0, 0, DateTime.UtcNow, Dq: true));
    StoryDirector.Force(StoryKind.CarContact, 0);
    new StoryCards().Draw(c, W, H, sn, 5000);
    using var img3 = surf.Snapshot(); File.WriteAllBytes(Path.Combine(outDir, "contact_dq.png"), img3.Encode(SKEncodedImageFormat.Png, 92).ToArray());
    foreach (int ms in new[] { 850, 920, 980, 1010, 1060, 1150, 1400 })
    {
        c.Clear(); Background(c, W, H);
        StoryDirector.Force(StoryKind.CarContact, 0);
        var scd = new StoryCards(); for (int q = 0; q <= ms; q += 50) scd.Draw(c, W, H, sn, q);
        c.Clear(); Background(c, W, H); scd.Draw(c, W, H, sn, ms);
        using var imq = surf.Snapshot(); File.WriteAllBytes(Path.Combine(outDir, $"dq_{ms}.png"), imq.Encode(SKEncodedImageFormat.Png, 85).ToArray());
    }
    Console.WriteLine("contact ok");
}

// Your Race preview (2026-10-25): our own car picture instead of the press photo
{
    const int W = 1920, H = 1080;
    using var surf = SKSurface.Create(new SKImageInfo(W, H));
    var c = surf.Canvas; Background(c, W, H);
    var sn = snap0 with { SessionTypeName = "Race", CapturedAt = DateTime.UtcNow, Player = me, TotalLaps = 20, LapsRemaining = 12 };
    StoryDirector.Force(StoryKind.YourRace, 0);
    new StoryCards().Draw(c, W, H, sn, 5000);
    using var img = surf.Snapshot(); File.WriteAllBytes(Path.Combine(outDir, "yourrace.png"), img.Encode(SKEncodedImageFormat.Png, 92).ToArray());
    Console.WriteLine("your race ok");
}

// battle map preview (2026-10-25): zoomed on the fight, four places at Le Mans + the yellow flag callout
{
    using var surf = SKSurface.Create(new SKImageInfo(1300, 560));
    var c = surf.Canvas; c.Clear(new SKColor(60, 66, 74));
    var cars3 = new List<Driver> { me, ahead, behind };
    var others = Enumerable.Range(0, 20).Select(i => behind with { CarIdx = 40 + i, LapDistPct = i / 20f + 0.013f }).ToList();
    var baseSn = snap0 with { TrackDisplayName = "Circuit des 24 Heures du Mans", TrackRawName = "lemans full", Extras = snap0.Extras with { TrackLength = "13.5541 km" } };
    var ids = cars3.Select(d => d.CarIdx).ToList();
    float[] pcts = { 0.214f, 0.885f, 0.987f, 0.509f, 0.282f, 0.428f, 0.02f, 0.70f };
    for (int k2 = 0; k2 < 8; k2++)
    {
        var sn2 = baseSn with { ClassLeaderboard = new List<DriverClass> { new("GTP", "#000000", cars3.Select((d, i) => d with { LapDistPct = pcts[k2] + (i - 1) * 0.002f }).Concat(others).ToList()) } };
        var bm2 = new BattleMap();
        for (int f = 0; f < 40; f++) bm2.Draw(new SKNoDrawCanvas(1300, 560), 0, 0, sn2, ids, true, 1000 + f * 40);
        bm2.Draw(c, 10 + (k2 % 4) * 320, 10 + (k2 / 4) * 250, sn2, ids, true, 3000);
    }
    // yellow flag: a car stopped in the Porsche Curves just ahead of us
    var yf = new YellowFlagCallout();
    var meY = me with { LapDistPct = 0.86f };
    var stopped = behind with { CarIdx = 77, CarNumber = "59", LapDistPct = 0.88f, OffTrack = true };
    var snY = baseSn with { Player = meY, ClassLeaderboard = new List<DriverClass> { new("GTP", "#000000", new List<Driver> { meY, stopped }) } };
    yf.Tick(snY with { Flags = FlagState.Empty }, 1000); if (true) { using var imB = surf.Snapshot(); File.WriteAllBytes(Path.Combine(outDir, "battlemap.png"), imB.Encode(SKEncodedImageFormat.Png, 92).ToArray()); } else
    yf.Tick(snY with { Flags = FlagState.FromBitField(0x8) }, 1100);
    for (int f = 0; f < 20; f++) yf.Draw(new SKNoDrawCanvas(1300, 560), 650, 300, 1100 + f * 30);
    yf.Draw(c, 650, 300, 1800);
    using var img = surf.Snapshot(); File.WriteAllBytes(Path.Combine(outDir, "battlemap.png"), img.Encode(SKEncodedImageFormat.Png, 92).ToArray());
    Console.WriteLine("battle map ok");
}

// Race So Far preview (2026-10-25): 6 cars swapping places through 5 laps
{
    const int W = 1920, H = 1080;
    using var surf = SKSurface.Create(new SKImageInfo(W, H));
    var c = surf.Canvas; Background(c, W, H);
    var names = new[] { "K. Estre", "A. Ramorellos", "J. Haydu", "M. Rockenfeller", "L. Vanthoor", "N. Tandy" };
    var baseAt = DateTime.UtcNow;
    var rnd = new Random(3);
    float[] pace = { 1.000f, 0.990f, 1.006f, 0.996f, 1.010f, 1.004f };
    float[] dist = { 0.05f, 0.04f, 0.03f, 0.02f, 0.01f, 0f };
    RaceSnapshot sn = snap0;
    for (int f = 0; f < 5 * 400; f++)
    {
        var ds = new List<Driver>();
        for (int i = 0; i < 6; i++)
        {
            dist[i] += 0.0025f * pace[i] * (1f + (float)(rnd.NextDouble() - 0.5) * 0.08f);
            int lapsDone = (int)Math.Floor(dist[i]);
            ds.Add(me with { CarIdx = i == 1 ? 9 : 20 + i, AbbrevName = names[i], Name = names[i], CarNumber = (10 + i).ToString(), CurrentLap = lapsDone + 1, LapsCompleted = lapsDone, LapDistPct = dist[i] - lapsDone, PositionInClass = 0, BestLapTime = 100f, LastLapTime = 100f, IsInPit = false, Incidents = 0 });
        }
        var ordered = ds.OrderByDescending(d => d.LapsCompleted + d.LapDistPct).Select((d, k) => d with { PositionInClass = k + 1 }).ToList();
        var cl = new List<DriverClass> { new("GTP", "#000000", ordered) };
        sn = snap0 with { ClassLeaderboard = cl, SessionTypeName = "Race", SessionNum = 2, TrackRawName = "Preview Ring", CapturedAt = baseAt.AddMilliseconds(f * 260), TotalLaps = 20, LapsRemaining = 15, IsConnected = true, Phase = RacePhase.Broadcast, PlayerCarIdx = 9, Player = ordered.First(d => d.CarIdx == 9) };
        RaceDiary.Tick(sn);
    }
    if (RaceDiary.LiveChart("GTP") is { } lcx) Console.WriteLine("live points: " + lcx.Chart.Values.Sum(v => v.Count) + " now " + lcx.Now);
    StoryDirector.Force(StoryKind.RaceSoFar, 0);
    var sc = new StoryCards();
    sc.Draw(c, W, H, sn, 4000);
    using var img = surf.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 92);
    File.WriteAllBytes(Path.Combine(outDir, "racesofar.png"), data.ToArray());
    Console.WriteLine("race so far ok");
}

// flag cloth preview (2026-10-25)
{
    var r = new SessionBarRenderer();
    var m = typeof(SessionBarRenderer).GetMethod("FlagCloth", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    using var surf = SKSurface.Create(new SKImageInfo(1200, 120)); var c = surf.Canvas; c.Clear(new SKColor(12, 14, 18));
    c.Scale(3);
    var cols = new[] { new SKColor(0x22, 0xC5, 0x5E), new SKColor(0xFA, 0xCC, 0x15), new SKColor(0xEF, 0x44, 0x44) };
