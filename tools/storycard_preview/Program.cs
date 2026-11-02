// Story card layouts preview (2026-10-27, #349): every story card, drawn by the REAL StoryCards code, in classic and the
// four right-third layouts (column / halves / flip / corners), at the moment the card is fully shown, over a neutral mock
// TV frame with the centre third (x 640..1280) marked by thin dashed lines. One 1920x1080 PNG per card per layout, a
// contact sheet per layout, and a per-frame managed-allocation check (each layout vs classic).
// Usage: storycard_preview OUT_DIR [--only RaceSoFar,PitStops] [--layouts classic,flip] [--noalloc] [--placeholder-logos] [--mini]
// --mini (#339): instead, every card as a MINI battle card over a battle frame drawn by the real renderers (session bar in
// battle mode + ticker, battle card, battle map, the tower for reference), a contact sheet and mini_alloc.csv.
using System.Reflection;
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

string outDir = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "out";
string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var only = Arg("--only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var layoutsArg = Arg("--layouts")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
bool alloc = !args.Contains("--noalloc");
Directory.CreateDirectory(outDir);

StorySound.Enabled = false;
OverlayLog.Sink = _ => { };
if (Environment.GetEnvironmentVariable("GS_LANDMARKS") is { Length: > 0 } lm) TrackCorners.LandmarkFileOverride = lm;
if (args.Contains("--placeholder-logos")) { Placeholder.MichelinIfMissing(); Placeholder.OwnerSponsors(); Placeholder.CarsIfMissing(); Placeholder.MakersIfMissing(); Placeholder.MichelinManIfMissing(); }

var snap = RaceSim.Run();
var me = snap.Player!;
Console.WriteLine($"sim: {snap.ClassLeaderboard.Sum(c => c.Drivers.Count)} cars, lap {snap.Leader?.LapsCompleted}, stops {RaceDiary.Stops.Count}, offs {RaceDiary.TotalOffs}, contacts {RaceDiary.Contacts.Count}, you P{me.PositionInClass} (grid P{RaceDiary.GridPos(me.CarIdx)})");

// the cards: every StoryKind, plus the Trading Paints DQ card (Car Contact with a disqualification)
var cards = new List<(string Name, StoryKind Kind, string Weather, Action? Setup)>
{
    ("RaceSoFar", StoryKind.RaceSoFar, "", null), ("YourRace", StoryKind.YourRace, "", null), ("MoveOfRace", StoryKind.MoveOfRace, "", null),
    ("FastestLaps", StoryKind.FastestLaps, "", null), ("PitStops", StoryKind.PitStops, "", null), ("Halfway", StoryKind.Halfway, "", null),
    ("WeatherWatch", StoryKind.WeatherWatch, "HEATING", null), ("DriverOfDay", StoryKind.DriverOfDay, "", null),
    ("CarContact", StoryKind.CarContact, "", null), ("Limits", StoryKind.Limits, "", null), ("Cleanest", StoryKind.Cleanest, "", null),
    ("DQ", StoryKind.CarContact, "", RaceSim.AddDq),
};
if (only != null) cards = cards.Where(c => only.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
// layouts are set through reflection by name, so the same tool also runs against an older build (classic only) to prove
// the classic card is pixel-for-pixel unchanged
var layoutField = typeof(StoryCards).GetField("Layout", BindingFlags.Public | BindingFlags.Static);
var layouts = new[] { "Classic", "Column", "Halves", "Flip", "Corners" };
if (layoutField == null) layouts = new[] { "Classic" };
if (layoutsArg != null) layouts = layouts.Where(l => layoutsArg.Contains(l, StringComparer.OrdinalIgnoreCase)).ToArray();

const int W = 1920, H = 1080;
using var surf = SKSurface.Create(new SKImageInfo(W, H));
var c = surf.Canvas;
// #412: square corners are a preview - on here unless --slanted (the app keeps them off until Angelo approves)
typeof(StoryCards).GetField("SquareCorners", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, !args.Contains("--slanted"));
if (args.Contains("--themes")) { Fixes.RunThemes(outDir, snap, surf, layoutField); return; }
if (args.Contains("--storybench")) { StoryBench.Run(outDir, snap, layoutField, args, cards); return; }   // #604
if (args.Contains("--pitbench")) { PitBench.Run(outDir, snap, layoutField, args); return; }   // #505
if (args.Contains("--roomshots")) { PitBench.RoomShots(outDir, snap, layoutField); return; }     // #549
if (args.Contains("--gofast")) { PitBench.GoFast(outDir, snap, layoutField, args); return; }     // #508
if (args.Contains("--cutclip")) { CutClip.Run(outDir, snap); return; }                           // #510
if (args.Contains("--pitclip")) { PitBench.Clip(outDir, snap, layoutField, args); return; }     // #505
if (args.Contains("--michelin")) { Fixes.RunMichelin(outDir, snap, surf, layoutField); return; }
if (args.Contains("--tweaks")) { Fixes.RunTweaks(outDir, snap, surf, layoutField); return; }
if (args.Contains("--fixes")) { Fixes.Run(outDir, snap, surf, layoutField); return; }
if (args.Contains("--limits")) { Fixes.Limits(outDir, snap, surf, layoutField); return; }
if (args.Contains("--radio")) { foreach (var k in cards.Where(k => k.Setup == null && !StoryCards.IsSpecial(k.Kind))) RadioPreview.Run(outDir, snap, surf, k.Kind, k.Name); return; }   // #517
if (args.Contains("--mini")) { MiniPreview.Run(outDir, snap, cards.Select(k => (k.Name, k.Kind, k.Weather, k.Setup)).ToList(), surf, alloc); return; }
var allocRows = new List<string>();
foreach (var layout in layouts)
{
    if (layoutField != null) layoutField.SetValue(null, Enum.Parse(layoutField.FieldType, layout));
    string ln = layout.ToLowerInvariant();
    string dir = Path.Combine(outDir, ln);
    Directory.CreateDirectory(dir);
    var shots = new List<(string Label, SKImage Img)>();
    foreach (var card in cards)
    {
        card.Setup?.Invoke();
        // when the card is fully in: classic / column / halves / corners at 4.0 s; flip shows page 1 then page 2
        var moments = new List<(string Suffix, double Ms)>();
        StoryDirector.Force(card.Kind, 0, card.Weather);
        double flipAt = Math.Max(4.2, StoryDirector.ExitAtMs / 2000.0);
        if (layout == "Flip") { moments.Add(("_p1", 4000)); moments.Add(("_p2", (flipAt + 3.3) * 1000)); }
        else moments.Add(("", 4000));
        var sc = new StoryCards();
        foreach (var (suffix, ms) in moments)
        {
            StoryDirector.Force(card.Kind, 0, card.Weather);
            // play the card up to the moment (on a no-draw canvas), so anything that runs once on the way (the DQ stamp) has run
            using (var nd = new SKNoDrawCanvas(W, H)) for (double q = 0; q < ms; q += 100) sc.Draw(nd, W, H, snap, q);
            Mock.Frame(c, W, H, snap);
            sc.Draw(c, W, H, snap, ms);
            Mock.Overlay(c, W, H, $"{LayoutTitle(layout)}  ·  {card.Name}{(suffix == "_p1" ? "  ·  page 1" : suffix == "_p2" ? "  ·  page 2" : "")}");
            var img = surf.Snapshot();
            using (var data = img.Encode(SKEncodedImageFormat.Png, 90)) File.WriteAllBytes(Path.Combine(dir, $"{card.Name}{suffix}.png"), data.ToArray());
            shots.Add((card.Name + (suffix == "_p1" ? " (1/2)" : suffix == "_p2" ? " (2/2)" : ""), img));
        }
        if (alloc)
        {
            // steady state: 3..4 s into the card, after a warm-up pass; managed bytes per frame on the render thread
            StoryDirector.Force(card.Kind, 0, card.Weather);
            using var nd = new SKNoDrawCanvas(W, H);
            for (double q = 0; q < 4000; q += 50) sc.Draw(nd, W, H, snap, q);
            StoryDirector.Force(card.Kind, 0, card.Weather);
            for (double q = 0; q < 3000; q += 50) sc.Draw(nd, W, H, snap, q);
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            int n = 0;
            for (double q = 3000; q < 4000; q += 50, n++) sc.Draw(nd, W, H, snap, q);
            long per = (GC.GetAllocatedBytesForCurrentThread() - b0) / n;
            allocRows.Add($"{ln},{card.Name},{per}");
        }
        RaceSim.UndoDq();
    }
    ContactSheet(Path.Combine(outDir, $"contact_{ln}.png"), LayoutTitle(layout), shots);
    foreach (var s in shots) s.Img.Dispose();
    Console.WriteLine($"{ln}: {shots.Count} frames");
}
// ---- mixed (#393, the default): regular cards in a row, each taking the next variation, then the special partner cards
// (Michelin pit tower, GO FAST Fastest Laps one page per class, GO FAST On the Limit) in the tall column
if (layoutField != null && Enum.GetNames(layoutField.FieldType).Contains("Mixed") && (layoutsArg == null || layoutsArg.Contains("mixed", StringComparer.OrdinalIgnoreCase)))
{
    layoutField.SetValue(null, Enum.Parse(layoutField.FieldType, "Mixed"));
    var shownProp = typeof(StoryCards).GetProperty("ShownLayout")!;
    string dir = Path.Combine(outDir, "mixed");
    Directory.CreateDirectory(dir);
    var shots = new List<(string Label, SKImage Img)>();
    var msc = new StoryCards();     // one renderer for the whole sequence, as on air: the rotation carries on card to card
    double clock = 0;
    int seq = 0;
    void Shot(StoryCards sc, string name, string label, double startMs, double atMs, bool playThrough)
    {
        if (playThrough) using (var nd = new SKNoDrawCanvas(W, H)) for (double q = startMs; q < atMs; q += 100) sc.Draw(nd, W, H, snap, q);
        Mock.Frame(c, W, H, snap);
        sc.Draw(c, W, H, snap, atMs);
        Mock.Overlay(c, W, H, $"MIXED  ·  {label}");
        var img = surf.Snapshot();
        using (var data = img.Encode(SKEncodedImageFormat.Png, 90)) File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
        shots.Add((label, img));
    }
    var regular = new (string Name, StoryKind Kind, string Weather, Action? Setup)[]
    {
        ("RaceSoFar", StoryKind.RaceSoFar, "", null), ("YourRace", StoryKind.YourRace, "", null), ("MoveOfRace", StoryKind.MoveOfRace, "", null),
        ("Halfway", StoryKind.Halfway, "", null), ("WeatherWatch", StoryKind.WeatherWatch, "HEATING", null), ("CarContact", StoryKind.CarContact, "", null),
        ("Cleanest", StoryKind.Cleanest, "", null), ("DQ", StoryKind.CarContact, "", RaceSim.AddDq),
    };
    foreach (var card in regular)
    {
        if ((bool)typeof(StoryCards).GetMethod("IsSpecial")!.Invoke(null, new object[] { card.Kind })!) continue;
        card.Setup?.Invoke();
        clock += 60000;
        StoryDirector.Force(card.Kind, clock, card.Weather);
        double flipAt = Math.Max(4.2, (StoryDirector.ExitAtMs - clock) / 2000.0);
        using (var nd = new SKNoDrawCanvas(W, H)) msc.Draw(nd, W, H, snap, clock);   // first frame picks the variation
        string v = shownProp.GetValue(msc)!.ToString()!;
        seq++;
        Shot(msc, $"{seq:00}_{card.Name}_{v.ToLowerInvariant()}", $"{seq}. {card.Name} ({v})", clock, clock + 4000, true);
        if (v == "Flip") Shot(msc, $"{seq:00}_{card.Name}_{v.ToLowerInvariant()}_p2", $"{seq}. {card.Name} ({v}, page 2)", clock + 4000, clock + (flipAt + 3.3) * 1000, true);
        RaceSim.UndoDq();
    }
    // the special cards
    foreach (var (name, kind) in new[] { ("PitStops", StoryKind.PitStops), ("FastestLaps", StoryKind.FastestLaps), ("Limits", StoryKind.Limits) })
    {
        clock += 60000;
        StoryDirector.Force(kind, clock, "");
        using (var nd = new SKNoDrawCanvas(W, H)) msc.Draw(nd, W, H, snap, clock);
        string v = shownProp.GetValue(msc)!.ToString()!;
        if (kind == StoryKind.FastestLaps)
        {
            int classes = snap.ClassLeaderboard.Count(cl => RaceDiary.Fastest(cl.Name) != null);
            double per = (StoryDirector.ExitAtMs - clock) / 1000.0 / Math.Max(1, classes);
            Console.WriteLine($"mixed: Fastest Laps {classes} pages x {per:0.0} s (card now {(StoryDirector.ExitAtMs - clock) / 1000.0 + StoryDirector.ExitMs / 1000.0:0.0} s)");
            double from = clock;
            for (int pg = 0; pg < classes; pg++)
            {
                double at = clock + (pg * per + Math.Min(3.2, per - 0.4)) * 1000;
                Shot(msc, $"special_{name}_page{pg + 1}", $"SPECIAL  {name} page {pg + 1}/{classes} ({v})", from, at, true);
                from = at;
            }
        }
        else Shot(msc, $"special_{name}", $"SPECIAL  {name} ({v})", clock, clock + 4000, true);
    }
    ContactSheet(Path.Combine(outDir, "contact_mixed.png"), "MIXED (default) - rotation + special cards", shots);
    foreach (var s2 in shots) s2.Img.Dispose();
    Console.WriteLine($"mixed: {shots.Count} frames");
}

if (alloc)
{
    File.WriteAllLines(Path.Combine(outDir, "alloc_per_frame.csv"), new[] { "layout,card,bytes_per_frame" }.Concat(allocRows));
    Console.WriteLine("managed bytes per frame (steady state, 3-4 s in):");
    var byCard = allocRows.Select(r => r.Split(',')).GroupBy(p => p[1]);
    Console.WriteLine($"  {"card",-13}" + string.Join("", layouts.Select(l => $"{l.ToLowerInvariant(),10}")));
    foreach (var g in byCard)
        Console.WriteLine($"  {g.Key,-13}" + string.Join("", layouts.Select(l => $"{(g.FirstOrDefault(p => p[0] == l.ToLowerInvariant())?[2] ?? "-"),10}")));
}

static string LayoutTitle(string l) => l switch
{
    "Classic" => "TODAY (classic)",
    "Column" => "A  TALL COLUMN",
    "Halves" => "B  TWO HALVES",
    "Flip" => "C  FLIP CARD",
    _ => "D  TOP + BOTTOM CORNERS",
};

static void ContactSheet(string path, string title, List<(string Label, SKImage Img)> shots)
{
    const int Cols = 4, TW = 480, TH = 270, Pad = 12, Head = 64, Cap = 30;
    int rows = (shots.Count + Cols - 1) / Cols;
    int w = Cols * TW + (Cols + 1) * Pad, h = Head + rows * (TH + Cap + Pad) + Pad;
    using var s = SKSurface.Create(new SKImageInfo(w, h));
    var c = s.Canvas; c.Clear(new SKColor(16, 18, 24));
    using var tp = new SKPaint { IsAntialias = true, Color = SKColors.White, TextSize = 34, Typeface = Fonts.BarlowCondensed.Bold };
    c.DrawText(title + "  -  every story card", Pad, 44, tp);
    using var lp = new SKPaint { IsAntialias = true, Color = new SKColor(200, 206, 216), TextSize = 20, Typeface = Fonts.Inter.SemiBold };
    using var ip = new SKPaint { FilterQuality = SKFilterQuality.High };
    for (int i = 0; i < shots.Count; i++)
    {
        float x = Pad + (i % Cols) * (TW + Pad), y = Head + (i / Cols) * (TH + Cap + Pad);
        c.DrawImage(shots[i].Img, new SKRect(x, y, x + TW, y + TH), ip);
        c.DrawText(shots[i].Label, x + 2, y + TH + 22, lp);
    }
    using var img = s.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 90);
    File.WriteAllBytes(path, data.ToArray());
}

/// <summary>A made-up race run through the real RaceDiary: a 3-class field at Le Mans, grid shuffles, one pit stop per
/// car, trips off the track (most at one corner), a contact between you and the car ahead, the track heating up.</summary>
static class RaceSim
{
    static readonly (string Cls, string Color, (string Name, string Abbrev, string Num, string Brand, string Car)[] Cars, float Lap)[] Field =
    {
        ("GTP", "#E8C26A", new[]
        {
            ("Marco Albera", "M. Albera", "5", "Acura", "Acura ARX-06 GTP"), ("Tobias Blom", "T. Blom", "01", "Cadillac", "Cadillac V-Series.R GTP"),
            ("Nate Tanner", "N. Tanner", "6", "Porsche", "Porsche 963 GTP"), ("Rory Tayler", "R. Tayler", "10", "Acura", "Acura ARX-06 GTP"),
            ("Angelo Morell", "A. Morell", "29", "BMW", "BMW M Hybrid V8"), ("Mattis Jansen", "M. Jansen", "7", "Porsche", "Porsche 963 GTP"),
            ("Paulo Deran", "P. Deran", "31", "Cadillac", "Cadillac V-Series.R GTP"), ("Lukas Brandt", "L. Brandt", "24", "BMW", "BMW M Hybrid V8"),
        }, 212f),
        ("LMP2", "#3B82F6", new[]
        {
            ("Bert Keaton", "B. Keaton", "2", "Oreca", "Dallara P217 LMP2"), ("Pierre Chalon", "P. Chalon", "04", "Oreca", "Dallara P217 LMP2"),
            ("Stefan Thorne", "S. Thorne", "11", "Oreca", "Dallara P217 LMP2"), ("Glen Kurtzman", "G. Kurtzman", "44", "Oreca", "Dallara P217 LMP2"),
            ("Niko Pinter", "N. Pinter", "8", "Oreca", "Dallara P217 LMP2"),
        }, 222f),
        ("GT3", "#34D399", new[]
        {
            ("Andres Garza", "A. Garza", "3", "Chevrolet", "Chevrolet Corvette Z06 GT3.R"), ("Jordy Tallis", "J. Tallis", "81", "BMW", "BMW M4 GT3"),
            ("Lars Heine", "L. Heine", "9", "Porsche", "Porsche 911 GT3 R (992)"), ("Dario Serrano", "D. Serrano", "62", "Ferrari", "Ferrari 296 GT3"),
            ("Kai Bachmann", "K. Bachmann", "77", "Porsche", "Porsche 911 GT3 R (992)"), ("Alessio Pieri", "A. Pieri", "82", "Ferrari", "Ferrari 296 GT3"),
            ("Owen Marsh", "O. Marsh", "14", "Mercedes", "Mercedes-AMG GT3"),
        }, 238f),
    };
    const int PlayerIdx = 4;          // car index of A. Morell (GTP, starts P5)
    static int contactOther = -1;

    public static RaceSnapshot Run()
    {
        var rnd = new Random(349);
        var cars = new List<(int Idx, int Cls, int Row, float Pace, double Dist, int PitLap, double PitLeft, bool Pitted, int Inc, double Best, double LapStart, double Last, int OffLeft)>();
        int idx = 0;
        for (int ci = 0; ci < Field.Length; ci++)
            for (int k = 0; k < Field[ci].Cars.Length; k++, idx++)
            {
                // pace: the player and a couple of others are quicker than where they start
                float pace = 1f - (idx == PlayerIdx ? 0.012f : k == 3 || k == 5 ? 0.008f : (float)(rnd.NextDouble() * 0.01 - 0.003)) + k * 0.0012f;
                cars.Add((idx, ci, k, pace, -k * 0.004 - ci * 0.03, 5 + rnd.Next(4), 0, false, 0, 0, 0, 0, 0));
            }
        var grid = new Dictionary<int, int>();
        foreach (var cr in cars) grid[cr.Idx] = cr.Row + 1;
        var t0 = new DateTime(2026, 10, 27, 14, 0, 0, DateTimeKind.Utc);
        RaceSnapshot snap = RaceSnapshot.Disconnected;
        const double Dt = 0.5;
        bool contactDone = false;
        for (int step = 0; step < 14 * 215 * 2; step++)
        {
            double now = step * Dt;
            for (int i = 0; i < cars.Count; i++)
            {
                var cr = cars[i];
                float lapT = Field[cr.Cls].Lap * cr.Pace;
                double v = Dt / lapT * (1 + (rnd.NextDouble() - 0.5) * 0.02);
                if (cr.PitLeft > 0) { v *= 0.25; cr.PitLeft -= Dt; }
                int before = (int)Math.Floor(cr.Dist);
                if (!cr.Pitted && cr.Dist + v >= cr.PitLap + 0.985) { cr.Pitted = true; cr.PitLeft = 26 + rnd.NextDouble() * 14; }
                cr.Dist += v;
                int after = (int)Math.Floor(cr.Dist);
                if (after > before && after >= 1)
                {
                    double lap = now - cr.LapStart;
                    lap += rnd.NextDouble() * 0.5;   // timing finer than the sim step
                    if (before >= 1 && cr.PitLeft <= 0) { cr.Last = lap; if (cr.Best == 0 || lap < cr.Best) cr.Best = lap; }
                    cr.LapStart = now;
                }
                // trips off the track: mostly at one corner (about 62% round the lap), a few anywhere
                if (cr.OffLeft > 0) cr.OffLeft--;
                else if (cr.Dist > 1 && cr.PitLeft <= 0)
                {
                    double pct = cr.Dist - Math.Floor(cr.Dist);
                    bool hot = Math.Abs(pct - 0.62) < 0.004 && rnd.NextDouble() < 0.05;
                    bool any = rnd.NextDouble() < 0.00018;
                    if (hot || any) { cr.OffLeft = 2; cr.Inc += 1; }
                }
                cars[i] = cr;
            }
            // the contact: you and the car just ahead, lap 9, both pick up 4x
            if (!contactDone && cars[PlayerIdx].Dist > 9.30)
            {
                var order = cars.Where(x => x.Cls == 0).OrderByDescending(x => x.Dist).ToList();
                int at = order.FindIndex(x => x.Idx == PlayerIdx);
                var other = order[Math.Max(0, at - 1)];
                if (other.Idx == PlayerIdx) other = order[at + 1];
                int oi = cars.FindIndex(x => x.Idx == other.Idx);
                var o = cars[oi]; o.Dist = cars[PlayerIdx].Dist + 0.002; o.Inc += 4; cars[oi] = o;
                var p = cars[PlayerIdx]; p.Inc += 4; cars[PlayerIdx] = p;
                contactOther = other.Idx;
                contactDone = true;
            }
            snap = Build(cars, grid, t0.AddSeconds(now + rnd.NextDouble() * 0.45), now);
            RaceDiary.Tick(snap);
        }
        return snap;
    }

    static RaceSnapshot Build(List<(int Idx, int Cls, int Row, float Pace, double Dist, int PitLap, double PitLeft, bool Pitted, int Inc, double Best, double LapStart, double Last, int OffLeft)> cars,
                              Dictionary<int, int> grid, DateTime at, double now)
    {
        var classes = new List<DriverClass>();
        var all = new List<Driver>();
        int overall = 1;
        var byDist = cars.OrderByDescending(x => x.Dist).Select(x => x.Idx).ToList();
        for (int ci = 0; ci < Field.Length; ci++)
        {
            var mine = cars.Where(x => x.Cls == ci).OrderByDescending(x => x.Dist).ToList();
            var ds = new List<Driver>();
            for (int k = 0; k < mine.Count; k++)
            {
                var cr = mine[k];
                var f = Field[ci].Cars[cr.Row];
                float lapT = Field[ci].Lap;
                int laps = Math.Max(0, (int)Math.Floor(cr.Dist));
                double gap = (mine[0].Dist - cr.Dist) * lapT, interval = k == 0 ? 0 : (mine[k - 1].Dist - cr.Dist) * lapT;
                ds.Add(new Driver(cr.Idx, f.Name, f.Abbrev, f.Abbrev[..1] + f.Name.Split(' ')[^1][..1], f.Num, f.Car, f.Brand, Field[ci].Cls, Field[ci].Color,
                    "A 4.2", "#0153DB", byDist.IndexOf(cr.Idx) + 1, k + 1, laps + 1, (float)Math.Max(0, cr.Dist - Math.Floor(cr.Dist)), laps, 0,
                    (float)cr.Best, (float)cr.Last, gap, interval, cr.PitLeft > 0, true, false, false, 2400 + cr.Idx * 37,
                    Incidents: cr.Inc, OffTrack: cr.OffLeft > 0));
                overall++;
            }
            all.AddRange(ds);
            classes.Add(new DriverClass(Field[ci].Cls, Field[ci].Color, ds));
        }
        var player = all.First(d => d.CarIdx == PlayerIdx);
        var leader = classes[0].Drivers[0];
        float trackC = 31f + (float)Math.Min(4.2, now / 600.0);
        return RaceSnapshot.Disconnected with
        {
            IsConnected = true, CapturedAt = at, SessionState = 4, SessionTypeName = "Race", Phase = RacePhase.Broadcast, Flags = FlagState.Empty,
            CurrentLap = leader.CurrentLap, TotalLaps = 24, LapsRemaining = Math.Max(0, 24 - leader.LapsCompleted), SessionTimeRemaining = 3600,
            SessionTimeOfDay = 15 * 3600 + now, AirTempC = 23f, TrackTempC = trackC, TemperatureUnit = "C", IsRaining = false,
            TrackRawName = "lemans full", TrackDisplayName = "Circuit des 24 Heures du Mans", TrackBroadcastName = "Le Mans", TrackCountryCode = "FR",
            OpponentsCount = all.Count, PlayerCarIdx = PlayerIdx, CameraCarIdx = PlayerIdx, Player = player, Leader = leader, CameraTarget = player,
            ClassLeaderboard = classes, SessionNum = 2, GridClassPositions = grid,
            Extras = SessionExtras.Empty with { Humidity = 0.55f, Skies = 1, WindMs = 3.2f, RainChancePct = 20, TrackLength = "13.63 km", TrackWetness = 1 },
        };
    }

    static System.Collections.IList Contacts => (System.Collections.IList)typeof(RaceDiary).GetField("contacts", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    static object? dqAdded;

    /// <summary>The Trading Paints DQ card: a car disqualified for incident points (added as the latest contact).</summary>
    public static void AddDq()
    {
        var gt3 = Field[2].Cars[6];
        int carIdx = Field[0].Cars.Length + Field[1].Cars.Length + 6;
        dqAdded = new RaceDiary.Contact(carIdx, gt3.Abbrev.ToUpperInvariant(), gt3.Brand, "GT3", 13, -1, 25, -1, null, null, 0, 0, DateTime.UtcNow, Dq: true);
        Contacts.Add(dqAdded);
    }

    public static void UndoDq()
    {
        if (dqAdded != null) { Contacts.Remove(dqAdded); dqAdded = null; }
    }
}

/// <summary>The mock TV picture behind the cards: sky, the road running into the distance, two cars in the middle,
/// a timing tower stand-in on the left and the session bar stand-in at the top; the centre third marked.</summary>
static class Mock
{
    public static void Frame(SKCanvas c, int w, int h, RaceSnapshot s)
    {
        using (var sky = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, h * 0.42f), new[] { new SKColor(0x7F, 0xA6, 0xCC), new SKColor(0xB4, 0xC8, 0xDC) }, null, SKShaderTileMode.Clamp) })
            c.DrawRect(0, 0, w, h * 0.42f, sky);
        using (var grass = new SKPaint { Color = new SKColor(0x3A, 0x3F, 0x46) }) c.DrawRect(0, h * 0.42f, w, h, grass);
        using (var road = new SKPath())
        {
            road.MoveTo(w * 0.47f, h * 0.42f); road.LineTo(w * 0.53f, h * 0.42f); road.LineTo(w * 0.86f, h); road.LineTo(w * 0.14f, h); road.Close();
            using var rp = new SKPaint { Color = new SKColor(0x2A, 0x2D, 0x33), IsAntialias = true }; c.DrawPath(road, rp);
        }
        using (var dash = new SKPaint { Color = new SKColor(230, 230, 230), StrokeWidth = 6 })
            for (float y = h * 0.45f; y < h; y += 70) c.DrawLine(w / 2f, y, w / 2f, y + 26, dash);
        Car(c, w / 2f - 20, h * 0.60f, 0.7f, new SKColor(0x1F, 0x5F, 0xD0));
        Car(c, w / 2f - 10, h * 0.84f, 1.25f, new SKColor(0xF2, 0xF2, 0xF2));
        // timing tower stand-in (left) and session bar stand-in (top centre)
        using (var tw = new SKPaint { Color = new SKColor(12, 16, 24, 230) }) c.DrawRect(new SKRect(20, 80, 300, 900), tw);
        using (var tt = new SKPaint { IsAntialias = true, Color = new SKColor(200, 206, 216), TextSize = 22, Typeface = Fonts.Inter.SemiBold })
        {
            int row = 0;
            foreach (var cl in s.ClassLeaderboard)
                foreach (var d in cl.Drivers)
                {
                    if (row >= 19) break;
                    tt.Color = d.CarIdx == s.PlayerCarIdx ? new SKColor(0xFF, 0xD1, 0x4A) : new SKColor(200, 206, 216);
                    c.DrawText($"{row + 1,2}  {d.AbbrevName}", 40, 120 + row * 40, tt);
                    row++;
                }
        }
        using (var sb = new SKPaint { Color = new SKColor(12, 16, 24, 235) }) c.DrawRect(new SKRect((w - 976) / 2f, 18, (w + 976) / 2f, 128), sb);
        using (var st = new SKPaint { IsAntialias = true, Color = SKColors.White, TextSize = 30, Typeface = Fonts.BarlowCondensed.Bold, TextAlign = SKTextAlign.Center })
            c.DrawText($"LAP {s.Leader?.CurrentLap}/{s.TotalLaps}   ·   LE MANS   ·   SESSION BAR", w / 2f, 84, st);
    }

    static void Car(SKCanvas c, float x, float y, float sc, SKColor col)
    {
        using var body = new SKPaint { IsAntialias = true, Color = col };
        using var dark = new SKPaint { IsAntialias = true, Color = new SKColor(20, 20, 24) };
        c.DrawRoundRect(new SKRect(x - 90 * sc, y - 34 * sc, x + 90 * sc, y + 10 * sc), 14 * sc, 14 * sc, body);
        c.DrawRoundRect(new SKRect(x - 50 * sc, y - 62 * sc, x + 50 * sc, y - 30 * sc), 12 * sc, 12 * sc, dark);
        c.DrawOval(new SKRect(x - 100 * sc, y - 4 * sc, x - 56 * sc, y + 30 * sc), dark);
        c.DrawOval(new SKRect(x + 56 * sc, y - 4 * sc, x + 100 * sc, y + 30 * sc), dark);
    }

    /// <summary>The centre third marked (thin dashed lines at x 640 and 1280) and a caption.</summary>
    public static void Overlay(SKCanvas c, int w, int h, string caption)
    {
        using var dash = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = new SKColor(0x34, 0xD3, 0x99, 230), PathEffect = SKPathEffect.CreateDash(new[] { 14f, 10f }, 0) };
        c.DrawLine(w / 3f, 0, w / 3f, h, dash); c.DrawLine(w * 2 / 3f, 0, w * 2 / 3f, h, dash);
        using var lp = new SKPaint { IsAntialias = true, Color = new SKColor(0x34, 0xD3, 0x99), TextSize = 22, Typeface = Fonts.Inter.Bold, TextAlign = SKTextAlign.Center };
        c.DrawText("CENTRE THIRD", w / 2f, h - 16, lp);
        using var cb = new SKPaint { Color = new SKColor(10, 12, 18, 220) };
        using var cp = new SKPaint { IsAntialias = true, Color = new SKColor(0xFF, 0xD1, 0x4A), TextSize = 30, Typeface = Fonts.BarlowCondensed.Bold };
        float tw = cp.MeasureText(caption);
        c.DrawRect(new SKRect(20, h - 66, 20 + tw + 36, h - 20), cb);
        c.DrawText(caption, 38, h - 32, cp);
    }
}

/// <summary>A plain grey stand-in for Angelo's michelin.png (it says LOGO) in this tool's own home folder, only when
/// there is none, so the tower draws its logo branch (MICHELIN on the pylon) on a machine without his picture.</summary>
static class Placeholder
{
    /// <summary>#398: grey stand-in silhouettes for GO FAST's 911 pictures (side + head-on) when the PNGs are missing, written
    /// into this run's own assets copy (bin/run/assets), so the space the car takes is visible in the previews.</summary>
    /// <summary>#399: grey 2:1 stand-in maker badges (the brand's name) where the real ones are missing, so the plates'
    /// badge + P-number + race-number layout is exercised at full width.</summary>
    public static void MakersIfMissing()
    {
        foreach (var brand in new[] { "BMW", "Ferrari", "Mercedes", "Porsche", "Chevrolet", "Acura", "Cadillac", "Oreca" })
        {
            var slug = ManufacturerBrand.Resolve(brand);
            if (slug == null) continue;
            var path = Assets.Resolve("logos", "manufacturers", slug + ".png");
            if (File.Exists(path)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var s = SKSurface.Create(new SKImageInfo(240, 120));
            var c = s.Canvas; c.Clear(SKColors.Transparent);
            using (var bp = new SKPaint { IsAntialias = true, Color = new SKColor(120, 126, 136) }) c.DrawRoundRect(new SKRect(4, 4, 236, 116), 18, 18, bp);
            using (var tp = new SKPaint { IsAntialias = true, Color = SKColors.White, TextSize = 44, TextAlign = SKTextAlign.Center, Typeface = SKTypeface.FromFamilyName(null, SKFontStyle.Bold) })
                c.DrawText(brand.ToUpperInvariant()[..Math.Min(4, brand.Length)], 120, 76, tp);
            using var img = s.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
    }

    /// <summary>#401: a white stand-in figure (stacked tyres, says MAN) for "michelin man full.png" in this tool's own
    /// corner_logos, only when there is none.</summary>
    public static void MichelinManIfMissing()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "corner_logos");
        var path = Path.Combine(dir, "michelin man full.png");
        if (File.Exists(path) || File.Exists(Path.Combine(dir, "michelin man.png"))) return;
        Directory.CreateDirectory(dir);
        using var s = SKSurface.Create(new SKImageInfo(300, 600));
        var c = s.Canvas; c.Clear(SKColors.Transparent);
        using var wp = new SKPaint { IsAntialias = true, Color = new SKColor(245, 245, 245) };
        using var op = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4, Color = new SKColor(60, 70, 90) };
        float[] ys = { 70, 170, 245, 315, 385, 455, 530 }; float[] ws = { 110, 150, 190, 200, 180, 150, 170 };
        for (int i = 0; i < ys.Length; i++) { var e = new SKRect(150 - ws[i] / 2, ys[i] - 45, 150 + ws[i] / 2, ys[i] + 45); c.DrawOval(e, wp); c.DrawOval(e, op); }
        using (var tp = new SKPaint { IsAntialias = true, Color = new SKColor(40, 60, 120), TextSize = 46, TextAlign = SKTextAlign.Center, Typeface = SKTypeface.FromFamilyName(null, SKFontStyle.Bold) })
            c.DrawText("MAN", 150, 330, tp);
        using var img = s.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    public static void CarsIfMissing()
    {
        foreach (var (file, side) in new[] { ("gofast_911_side.png", true), ("gofast_911_front.png", false) })
        {
            var path = Assets.Resolve("logos", "sponsors", file);
            if (File.Exists(path)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            int w = 1200, h = side ? 400 : 540;
            using var s = SKSurface.Create(new SKImageInfo(w, h));
            var c = s.Canvas; c.Clear(SKColors.Transparent);
            using var body = new SKPaint { IsAntialias = true, Color = new SKColor(150, 156, 166) };
            using var dark = new SKPaint { IsAntialias = true, Color = new SKColor(40, 44, 52) };
            using var path2 = new SKPath();
            if (side)
            {
                path2.MoveTo(40, 300); path2.CubicTo(60, 230, 200, 210, 380, 190); path2.CubicTo(520, 110, 760, 100, 900, 170);
                path2.CubicTo(1040, 190, 1160, 220, 1170, 300); path2.LineTo(40, 300); path2.Close();
                c.DrawPath(path2, body);
                c.DrawCircle(250, 310, 70, dark); c.DrawCircle(960, 310, 70, dark);
            }
            else
            {
                path2.MoveTo(60, 430); path2.CubicTo(70, 300, 220, 250, 420, 220); path2.CubicTo(520, 120, 680, 120, 780, 220);
                path2.CubicTo(980, 250, 1130, 300, 1140, 430); path2.LineTo(60, 430); path2.Close();
                c.DrawPath(path2, body);
                c.DrawOval(new SKRect(230, 300, 390, 360), dark); c.DrawOval(new SKRect(810, 300, 970, 360), dark);
                c.DrawRect(new SKRect(90, 430, 250, 500), dark); c.DrawRect(new SKRect(950, 430, 1110, 500), dark);
            }
            using var tp = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(200), TextSize = 46, TextAlign = SKTextAlign.Center, Typeface = SKTypeface.FromFamilyName(null, SKFontStyle.Bold) };
            c.DrawText("911 PLACEHOLDER", w / 2f, side ? 270 : 400, tp);
            using var img = s.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
    }

    /// <summary>The tool's own sponsors.json with Angelo's partner mapping (as on his PC: A&B, StormExhibit, GO FAST on
    /// Fastest Laps + On the Limit, Trading Paints), drawn words instead of logo files - so the special GO FAST cards show.</summary>
    public static void OwnerSponsors()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "sponsors.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
        {
          "partners": {
            "AB": { "on": true, "logo": "", "word": "" },
            "Storm": { "on": true, "logo": "", "word": "" },
            "GoFast": { "on": true, "logo": "", "word": "GO FAST" },
            "TradingPaints": { "on": true, "logo": "", "word": "TRADING PAINTS" },
            "Custom": { "on": false, "logo": "", "word": "" }
          },
          "presents": {
            "RaceSoFar": "AB", "MoveOfRace": "AB", "WeatherWatch": "Storm", "FastestLaps": "GoFast", "Limits": "GoFast",
            "CarContact": "TradingPaints", "Cleanest": "TradingPaints", "YourRace": "none", "PitStops": "none", "Halfway": "none", "DriverOfDay": "none"
          }
        }
        """);
    }

    public static void MichelinIfMissing()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "corner_logos");
        var path = Path.Combine(dir, "michelin.png");
        if (File.Exists(path)) return;
        Directory.CreateDirectory(dir);
        using var s = SKSurface.Create(new SKImageInfo(300, 90));
        var c = s.Canvas; c.Clear(SKColors.Transparent);
        using (var bp = new SKPaint { IsAntialias = true, Color = new SKColor(150, 150, 150) }) c.DrawRoundRect(new SKRect(2, 2, 298, 88), 10, 10, bp);
        using (var tp = new SKPaint { IsAntialias = true, Color = SKColors.White, TextSize = 52, TextAlign = SKTextAlign.Center, Typeface = SKTypeface.FromFamilyName(null, SKFontStyle.Bold) })
            c.DrawText("LOGO", 150, 64, tp);
        using var img = s.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }
}

/// <summary>#398 / #399 previews: the Trading Paints cards with two-digit positions (P2, P12, P18), long names and a
/// three-digit car number in every layout, and the GO FAST cards (On the Limit, Fastest Laps pages) in the column.</summary>
static class Fixes
{
    static System.Collections.IList Contacts => (System.Collections.IList)typeof(RaceDiary).GetField("contacts", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    static RaceSnapshot WithDriver(RaceSnapshot s, int carIdx, int pos, string name, string? number = null)
    {
        var cls = s.ClassLeaderboard.Select(cl => new DriverClass(cl.Name, cl.Color, cl.Drivers.Select(d => d.CarIdx == carIdx
            ? d with { PositionInClass = pos, Name = name, AbbrevName = name, CarNumber = number ?? d.CarNumber } : d).ToList())).ToList();
        return s with { ClassLeaderboard = cls };
    }

    public static void Run(string outDir, RaceSnapshot snap, SKSurface surf, FieldInfo? layoutField)
    {
        const int W = 1920, H = 1080;
        var c = surf.Canvas;
        Directory.CreateDirectory(outDir);
        var gt3 = snap.ClassLeaderboard[2].Drivers;
        int a = gt3[1].CarIdx, b = gt3[3].CarIdx, d = gt3[5].CarIdx;
        const string LongA = "Maximilian Vandenbroucke-Hoffmann", LongB = "Alessandro Castelfranchi", LongD = "Juan-Sebastian Montoya-Ribeiro";
        var cases = new List<(string Name, StoryKind Kind, RaceSnapshot S, object? Contact)>();
        var s1 = WithDriver(WithDriver(snap, a, 2, LongA, "117"), b, 12, LongB, "8");
        cases.Add(("contact_P2_P12", StoryKind.CarContact, s1, new RaceDiary.Contact(a, "M. VANDENBROUCKE-HOFFMANN", "BMW", "GT3", 12, 4, 6, b, "A. CASTELFRANCHI", "Ferrari", 4, 5, DateTime.UtcNow)));
        var s2 = WithDriver(WithDriver(snap, b, 12, LongB, "88"), d, 18, LongD, "177");
        cases.Add(("contact_P12_P18", StoryKind.CarContact, s2, new RaceDiary.Contact(b, "A. CASTELFRANCHI", "Ferrari", "GT3", 12, 4, 6, d, "J. MONTOYA-RIBEIRO", "Mercedes", 4, 9, DateTime.UtcNow)));
        cases.Add(("dq_P18", StoryKind.CarContact, s2, new RaceDiary.Contact(d, "J. MONTOYA-RIBEIRO", "Mercedes", "GT3", 13, -1, 25, -1, null, null, 0, 0, DateTime.UtcNow, Dq: true)));
        var clean = StoryDirector.Cleanest(snap.ClassLeaderboard.SelectMany(x => x.Drivers))!;
        cases.Add(("cleanest_P12", StoryKind.Cleanest, WithDriver(snap, clean.CarIdx, 12, LongA, "117"), null));
        var shots = new List<(string, SKImage)>();
        string[] layouts = layoutField != null ? new[] { "Classic", "Column", "Halves", "Flip", "Corners" } : new[] { "Classic" };
        foreach (var lay in layouts)
        {
            if (layoutField != null) layoutField.SetValue(null, Enum.Parse(layoutField.FieldType, lay));
            foreach (var cs in cases)
            {
                if (cs.Contact != null) Contacts.Add(cs.Contact);
                StoryDirector.Force(cs.Kind, 0, "");
                var sc = new StoryCards();
                using (var nd = new SKNoDrawCanvas(W, H)) for (double q = 0; q < 4000; q += 100) sc.Draw(nd, W, H, cs.S, q);
                Mock.Frame(c, W, H, cs.S);
                sc.Draw(c, W, H, cs.S, 4000);
                Mock.Overlay(c, W, H, $"#399  {lay}  ·  {cs.Name}");
                Save(outDir, $"tp_{lay.ToLowerInvariant()}_{cs.Name}", $"{lay} {cs.Name}", surf, shots);
                if (cs.Contact != null) Contacts.Remove(cs.Contact);
            }
        }
        Limits(outDir, snap, surf, layoutField, shots);
        // #401 the Michelin pit tower: classic, and the column (mixed: special cards always take it)
        foreach (var lay in layoutField != null ? new[] { "Classic", "Mixed" } : new[] { "Classic" })
        {
            if (layoutField != null) layoutField.SetValue(null, Enum.Parse(layoutField.FieldType, lay));
            StoryDirector.Force(StoryKind.PitStops, 0, "");
            var sc = new StoryCards();
            using (var nd = new SKNoDrawCanvas(W, H)) for (double q = 0; q < 4000; q += 100) sc.Draw(nd, W, H, snap, q);
            Mock.Frame(c, W, H, snap);
            sc.Draw(c, W, H, snap, 4000);
            Mock.Overlay(c, W, H, $"#401  Michelin pit tower  ·  {(lay == "Mixed" ? "column" : "classic")}");
            Save(outDir, $"michelin_{(lay == "Mixed" ? "column" : "classic")}", $"Michelin {(lay == "Mixed" ? "column" : "classic")}", surf, shots);
        }
        // GO FAST in the column (mixed: special cards always take it)
        if (layoutField != null)
        {
            layoutField.SetValue(null, Enum.Parse(layoutField.FieldType, "Mixed"));
            foreach (var (name, kind, ats) in new[] { ("limits", StoryKind.Limits, new[] { 4000.0 }), ("fastest", StoryKind.FastestLaps, new[] { 3200.0, 7200.0, 11200.0 }) })
            {
                StoryDirector.Force(kind, 0, "");
                var sc = new StoryCards();
                double from = 0;
                for (int i = 0; i < ats.Length; i++)
                {
                    using (var nd = new SKNoDrawCanvas(W, H)) for (double q = from; q < ats[i]; q += 100) sc.Draw(nd, W, H, snap, q);
                    from = ats[i];
                    Mock.Frame(c, W, H, snap);
                    sc.Draw(c, W, H, snap, ats[i]);
                    Mock.Overlay(c, W, H, $"#398  GO FAST  ·  {name}" + (ats.Length > 1 ? $" page {i + 1}" : ""));
                    Save(outDir, $"gofast_{name}" + (ats.Length > 1 ? $"_page{i + 1}" : ""), $"GO FAST {name}" + (ats.Length > 1 ? $" p{i + 1}" : ""), surf, shots);
                }
            }
        }
        foreach (var (_, img) in shots) img.Dispose();
        Console.WriteLine($"fixes: {shots.Count} frames in {outDir}");
    }

    /// <summary>#410 / #411 / #412: Fastest Laps column pages, On the Limit classic + column, three regular cards square vs slanted.</summary>
    public static void RunTweaks(string outDir, RaceSnapshot snap, SKSurface surf, FieldInfo? layoutField)
    {
        const int W = 1920, H = 1080;
        var c = surf.Canvas;
        Directory.CreateDirectory(outDir);
        var sq = typeof(StoryCards).GetField("SquareCorners", BindingFlags.Public | BindingFlags.Static);
        bool square0 = sq != null && (bool)sq.GetValue(null)!;
        var shots = new List<(string, SKImage)>();
        void Card(string file, string label, string lay, StoryKind kind, double[] ats, bool? square = null)
        {
            if (layoutField != null) layoutField.SetValue(null, Enum.Parse(layoutField.FieldType, lay));
            sq?.SetValue(null, square ?? square0);
            StoryDirector.Force(kind, 0, "");
            var sc = new StoryCards();
            double from = 0;
            for (int i = 0; i < ats.Length; i++)
            {
                using (var nd = new SKNoDrawCanvas(W, H)) for (double q = from; q < ats[i]; q += 100) sc.Draw(nd, W, H, snap, q);
                from = ats[i];
                Mock.Frame(c, W, H, snap);
                sc.Draw(c, W, H, snap, ats[i]);
                string suf = ats.Length > 1 ? $" page {i + 1}" : "";
                Mock.Overlay(c, W, H, label + suf);
                Save(outDir, file + (ats.Length > 1 ? $"_page{i + 1}" : ""), label + suf, surf, shots);
            }
        }
        Card("fastest_column", "#410 Fastest Laps (column)", "Mixed", StoryKind.FastestLaps, new[] { 3200.0, 7200.0, 11200.0 });
        Card("limits_classic", "#411 On the Limit (classic)", "Classic", StoryKind.Limits, new[] { 4200.0 });
        Card("limits_column", "#411 On the Limit (column)", "Mixed", StoryKind.Limits, new[] { 4200.0 });
        foreach (var (name, kind, lay) in new[] { ("RaceSoFar", StoryKind.RaceSoFar, "Column"), ("YourRace", StoryKind.YourRace, "Halves"), ("Halfway", StoryKind.Halfway, "Corners"), ("RaceSoFar", StoryKind.RaceSoFar, "Classic") })
            foreach (var square in new[] { false, true })
                Card($"corners_{name}_{lay.ToLowerInvariant()}_{(square ? "square" : "slanted")}", $"#412 {name} ({lay.ToLowerInvariant()}) {(square ? "SQUARE" : "slanted (today)")}", lay, kind, new[] { 4000.0 }, square);
        sq?.SetValue(null, square0);
        foreach (var (_, img) in shots) img.Dispose();
        Console.WriteLine($"tweaks: {shots.Count} frames in {outDir}");
    }

    /// <summary>#409 the running Michelin Man on the pit tower's stem (classic + column at 0.5 / 2 / 4 / 7.5 s) and #422 the
    /// cards' foot without the white + red cuts. GS_MICHELIN_RUN = a folder of frames (001.png..) copied into this tool's
    /// own Documents\iRacing\Grandstand\corner_logos\michelin_run when given.</summary>
    public static void RunMichelin(string outDir, RaceSnapshot snap, SKSurface surf, FieldInfo? layoutField)
    {
        const int W = 1920, H = 1080;
        var c = surf.Canvas;
        Directory.CreateDirectory(outDir);
        if (Environment.GetEnvironmentVariable("GS_MICHELIN_RUN") is { Length: > 0 } src && Directory.Exists(src))
        {
            var dst = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "corner_logos", "michelin_run");
            // 2026-10-27 (#427): NEVER delete here - on Angelo's PC this folder IS his real frames folder (src == dst wiped them)
            if (!string.Equals(Path.GetFullPath(src).TrimEnd('\\', '/'), Path.GetFullPath(dst).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(dst);
                foreach (var f in Directory.GetFiles(src, "*.png")) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            }
        }
        var mr = typeof(StoryCards).Assembly.GetType("ApexHUD.Native.Overlays.MichelinRun");
        mr?.GetField("Synchronous")?.SetValue(null, true);
        var shots = new List<(string, SKImage)>();
        void Card(string file, string label, string lay, StoryKind kind, double[] ats)
        {
            if (layoutField != null) layoutField.SetValue(null, Enum.Parse(layoutField.FieldType, lay));
            StoryDirector.Force(kind, 0, "");
            var sc = new StoryCards();
            double from = 0;
            foreach (var at in ats)
            {
                using (var nd = new SKNoDrawCanvas(W, H)) for (double q = from; q < at; q += 1000 / 15.0) sc.Draw(nd, W, H, snap, q);
                from = at;
                Mock.Frame(c, W, H, snap);
                sc.Draw(c, W, H, snap, at);
                string suf = ats.Length > 1 ? $"  ·  {at / 1000:0.0} s" : "";
                Mock.Overlay(c, W, H, label + suf);
                Save(outDir, file + (ats.Length > 1 ? $"_{at / 1000:0.0}s" : ""), label + suf, surf, shots);
            }
        }
        // 2026-10-27: --michelin-video = every frame (15 fps, 0-9 s) of the column pit card, as it plays on the TV
        if (Environment.GetCommandLineArgs().Contains("--michelin-video"))
        {
            if (layoutField != null) layoutField.SetValue(null, Enum.Parse(layoutField.FieldType, "Mixed"));
            StoryDirector.Force(StoryKind.PitStops, 0, "");
            var sv = new StoryCards();
            var vdir = Path.Combine(outDir, "video"); Directory.CreateDirectory(vdir);
            int k = 0;
            double vfps = Environment.GetCommandLineArgs().Contains("--fps60") ? 60 : 15;
            bool whole = Environment.GetCommandLineArgs().Contains("--whole");   // the whole card, through its exit
            if (whole) vfps = 30;
            double endMs = whole ? (StoryDirector.ExitAtMs - StoryDirector.StartedMs) + 1000 : (vfps > 30 ? 4000 : 9000);
            for (double q = 0; q <= endMs; q += 1000 / vfps, k++)
            {
                Mock.Frame(c, W, H, snap);
                sv.Draw(c, W, H, snap, q);
                using var img = surf.Snapshot();
                using var data = img.Encode(SKEncodedImageFormat.Png, 80);
                File.WriteAllBytes(Path.Combine(vdir, $"{k:0000}.png"), data.ToArray());
            }
            Console.WriteLine($"michelin video: {k} frames in {vdir}");
            return;
        }
        var times = new[] { 500.0, 2000.0, 4000.0, 7500.0 };
        Card("pit_classic", "#409 Pit Stops (classic)", "Classic", StoryKind.PitStops, times);
        if (layoutField != null) Card("pit_column", "#409 Pit Stops (column)", "Mixed", StoryKind.PitStops, times);
        foreach (var (name, kind, lay) in new[] { ("RaceSoFar", StoryKind.RaceSoFar, "Classic"), ("RaceSoFar", StoryKind.RaceSoFar, "Column"), ("CarContact", StoryKind.CarContact, "Halves"), ("Limits", StoryKind.Limits, "Mixed") })
            if (layoutField != null || lay == "Classic")
                Card($"foot_{name}_{lay.ToLowerInvariant()}", $"#422 {name} ({lay.ToLowerInvariant()})", lay, kind, new[] { 4000.0 });
        foreach (var (_, img) in shots) img.Dispose();
        Console.WriteLine($"michelin: {shots.Count} frames in {outDir}");
    }

    /// <summary>#402 / r4 On the Limit: full outline -> mid-zoom -> zoomed onto the hotspot (classic and the column); the map
    /// label and the HOTSPOT line name the same place.</summary>
    public static void Limits(string outDir, RaceSnapshot snap, SKSurface surf, FieldInfo? layoutField, List<(string, SKImage)>? into = null)
    {
        const int W = 1920, H = 1080;
        var c = surf.Canvas;
        var shots = into ?? new List<(string, SKImage)>();
        foreach (var lay in layoutField != null ? new[] { "Classic", "Mixed" } : new[] { "Classic" })
        {
            if (layoutField != null) layoutField.SetValue(null, Enum.Parse(layoutField.FieldType, lay));
            StoryDirector.Force(StoryKind.Limits, 0, "");
            var sc = new StoryCards();
            double from = 0;
            foreach (var (ms, tag) in new[] { (1800.0, "1_full"), (2600.0, "2_midzoom"), (4200.0, "3_zoomed") })
            {
                using (var nd = new SKNoDrawCanvas(W, H)) for (double q = from; q < ms; q += 100) sc.Draw(nd, W, H, snap, q);
                from = ms;
                Mock.Frame(c, W, H, snap);
                sc.Draw(c, W, H, snap, ms);
                string ln = lay == "Mixed" ? "column" : "classic";
                Mock.Overlay(c, W, H, $"On the Limit  ·  {ln}  ·  {tag[2..]} ({ms / 1000:0.0} s)");
                Save(outDir, $"limits_zoom_{ln}_{tag}", $"Limits {ln} {tag[2..]}", surf, shots);
            }
        }
        if (into == null) { foreach (var (_, img) in shots) img.Dispose(); Console.WriteLine($"limits: {shots.Count} frames in {outDir}"); }
    }

    /// <summary>#424: On the Limit with the hotspot at the PlayStation Chicane, the Dunlop chicane and a plain corner (offs
    /// moved there), classic + column, before / mid / after the zoom. Stand-in playstation / dunlop pictures (they switch
    /// the themes on) go into this tool's own corner_logos when missing.</summary>
    public static void RunThemes(string outDir, RaceSnapshot snap, SKSurface surf, FieldInfo? layoutField)
    {
        const int W = 1920, H = 1080;
        var c = surf.Canvas;
        Directory.CreateDirectory(outDir);
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "corner_logos");
        Directory.CreateDirectory(dir);
        foreach (var (file, col, word) in new[] { ("playstation.png", new SKColor(0, 0x70, 0xD1), "PS"), ("dunlop.png", new SKColor(0xFF, 0xCE, 0), "DUNLOP") })
        {
            var path = Path.Combine(dir, file);
            if (File.Exists(path)) continue;
            using var ps = SKSurface.Create(new SKImageInfo(300, 120));
            ps.Canvas.Clear(SKColors.Transparent);
            using (var bp = new SKPaint { IsAntialias = true, Color = col }) ps.Canvas.DrawRoundRect(new SKRect(4, 4, 296, 116), 14, 14, bp);
            using (var tp = new SKPaint { IsAntialias = true, Color = SKColors.Black, TextSize = 52, TextAlign = SKTextAlign.Center, Typeface = SKTypeface.FromFamilyName(null, SKFontStyle.Bold) })
                ps.Canvas.DrawText(word, 150, 78, tp);
            using var img = ps.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
        // where each place is on the lap (CrewChief landmarks / the built-in table)
        float Find(string word, string? avoid = null)
        {
            for (float p = 0.001f; p < 1; p += 0.001f)
                if (TrackCorners.Named(snap.TrackRawName, snap.Extras.TrackLength, p, snap.TrackBroadcastName) is { } n
                    && n.Contains(word, StringComparison.OrdinalIgnoreCase) && (avoid == null || !n.Contains(avoid, StringComparison.OrdinalIgnoreCase))) return p + 0.004f;
            return -1;
        }
        var offSpots = (List<(int CarIdx, float Pct)>)typeof(RaceDiary).GetField("offSpots", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var keep = offSpots.ToList();
        var shots = new List<(string, SKImage)>();
        foreach (var (tag, word) in new[] { ("playstation", "Playstation"), ("dunlop", "Dunlop"), ("plain", "Arnage") })
        {
            float at = Find(word);
            Console.WriteLine($"themes: {word} at {at:0.000} -> {TrackCorners.Named(snap.TrackRawName, snap.Extras.TrackLength, at, snap.TrackBroadcastName)}");
            if (at < 0) continue;
            offSpots.Clear();
            var rnd = new Random(7);
            for (int i = 0; i < 30; i++) offSpots.Add((keep[i % keep.Count].CarIdx, i < 22 ? at + (float)(rnd.NextDouble() - 0.5) * 0.006f : (float)rnd.NextDouble()));
            foreach (var lay in layoutField != null ? new[] { "Classic", "Mixed" } : new[] { "Classic" })
            {
                if (layoutField != null) layoutField.SetValue(null, Enum.Parse(layoutField.FieldType, lay));
                StoryDirector.Force(StoryKind.Limits, 0, "");
                var sc = new StoryCards();
                double from = 0;
                string ln = lay == "Mixed" ? "column" : "classic";
                foreach (var (ms, ph) in new[] { (1800.0, "1_before"), (2600.0, "2_mid"), (4200.0, "3_after") })
                {
                    using (var nd = new SKNoDrawCanvas(W, H)) for (double q = from; q < ms; q += 100) sc.Draw(nd, W, H, snap, q);
                    from = ms;
                    Mock.Frame(c, W, H, snap);
                    sc.Draw(c, W, H, snap, ms);
                    Mock.Overlay(c, W, H, $"#424 On the Limit  ·  {word}  ·  {ln}  ·  {ph[2..]}");
                    Save(outDir, $"theme_{tag}_{ln}_{ph}", $"{word} {ln} {ph[2..]}", surf, shots);
                }
            }
        }
        offSpots.Clear(); offSpots.AddRange(keep);
        foreach (var (_, img) in shots) img.Dispose();
        Console.WriteLine($"themes: {shots.Count} frames in {outDir}");
    }

    static void Save(string dir, string file, string label, SKSurface surf, List<(string, SKImage)> shots)
    {
        var img = surf.Snapshot();
        using (var data = img.Encode(SKEncodedImageFormat.Png, 90)) File.WriteAllBytes(Path.Combine(dir, file + ".png"), data.ToArray());
        shots.Add((label, img));
    }
}
