// Track pack preview (2026-10-28, IMSA season weeks 8-12): for each track, every named stop of the lap as the onboard
// label (theme + heritage line) on one contact sheet, and the track card (story text, facts, lap walk) at a few moments.
// Usage: dotnet track_pack_preview.dll OUT_ROOT LANDMARKS_JSON [track-slug ...]
// The backgrounds are PLACEHOLDERS (plain gradients). No track maps / logos / corner_logos on this machine, so brand
// themes (Dunlop, Casio, driver portraits) show their fallback here - re-render on the PC to see them.
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

string outRoot = args.Length > 0 ? args[0] : "out";
if (args.Length > 1) TrackCorners.LandmarkFileOverride = args[1];
var only = args.Skip(2).ToHashSet(StringComparer.OrdinalIgnoreCase);

// raw = iRacing WeekendInfo.TrackName as CrewChief lists it; length / turns are the published GP figures, used only to
// lay the preview out (on the PC iRacing supplies both)
var tracks = new (string Slug, string Raw, string Display, string Cc, string City, string Country, string Len, int Turns)[]
{
    ("mexico",      "mexicocity gp",        "Autódromo Hermanos Rodríguez",       "MX", "Mexico City", "Mexico",         "4.304 km", 17),
    ("suzuka",      "suzuka grandprix",     "Suzuka International Racing Course", "JP", "Suzuka",      "Japan",          "5.807 km", 18),
    ("silverstone", "silverstone 2019 gp",  "Silverstone Circuit",                "GB", "Silverstone", "United Kingdom", "5.891 km", 18),
    ("sebring",     "sebring international","Sebring International Raceway",      "US", "Sebring",     "USA",            "6.019 km", 17),
    ("monza",       "monza full",           "Autodromo Nazionale Monza",          "IT", "Monza",       "Italy",          "5.793 km", 11),
    // 2026-10-28 track packs (TrackData/tracks.json): length / turns from the research files
    ("spa",         "spa 2024 up",          "Circuit de Spa-Francorchamps",       "BE", "Stavelot",    "Belgium",        "7.004 km", 19),
    ("roadamerica", "roadamerica full",     "Road America",                       "US", "Elkhart Lake","USA",            "6.515 km", 14),
    ("nordschleife","nurburgring nordschleife","Nürburgring Nordschleife",        "DE", "Nürburg",     "Germany",        "20.82 km", 73),
    ("bathurst",    "bathurst",             "Mount Panorama Circuit",             "AU", "Bathurst",    "Australia",      "6.213 km", 23),
    ("interlagos",  "interlagos gp",        "Autódromo José Carlos Pace",         "BR", "São Paulo",   "Brazil",         "4.309 km", 15),
    ("brandshatch", "brandshatch grandprix","Brands Hatch",                       "GB", "West Kingsdown","United Kingdom","3.916 km", 9),
    // 2026-10-28 round 2: new research files + a hand-made track's other layout + Le Mans (the reference, unchanged)
    ("thruxton",    "thruxton",             "Thruxton Circuit",                   "GB", "Thruxton",    "United Kingdom", "3.793 km", 12),
    ("portland",    "portland full",        "Portland International Raceway",     "US", "Portland",    "USA",            "3.166 km", 12),
    ("magnycours",  "magnycours gp",        "Circuit de Nevers Magny-Cours",      "FR", "Magny-Cours", "France",         "4.411 km", 17),
    ("silverstone2011", "silverstone gp",   "Silverstone Circuit",                "GB", "Silverstone", "United Kingdom", "5.141 km", 0),
    ("lemans",      "lemans full",          "Circuit des 24 Heures du Mans",      "FR", "Le Mans",     "France",         "13.626 km", 38),
};

// 2026-10-28: "allstops" - every track-pack layout's lap as text (names, straights, themes, heritage) to OUT/all_stops.txt
if (only.Contains("allstops"))
{
    var all = new List<string>();
    foreach (var key in TrackPacks.Keys.OrderBy(k => k))
    {
        var pk = TrackPacks.For(key)!;
        var lay = TrackPacks.Layout(key);
        double km = lay.Km > 0 ? lay.Km : ((TrackCorners.Corners(key)?.LastOrDefault().EndM ?? 0) + 300) / 1000.0;
        string len = km.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " km";
        all.Add($"## {key}  ->  {TrackNames.Resolve(key)} [{TrackNames.ResolveInfo(key)?.CountryCode}]  first race: {TrackStory.FirstRace(key) ?? "-"}  len {len}");
        foreach (var st in TrackCorners.Lap(key, len) ?? new())
            all.Add($"   {(st.Straight ? "S" : "C")} {st.StartPct:0.000} {st.Name,-30} {CornerTheme.For(st.Name.ToUpperInvariant())?.Kind.ToString() ?? "-",-6} {CornerTheme.HeritageOf(key, st.Name) ?? ""}");
    }
    Directory.CreateDirectory(outRoot);
    File.WriteAllLines(Path.Combine(outRoot, "all_stops.txt"), all);
    only.Remove("allstops");
    if (only.Count == 0) return;
}

void Bg(SKCanvas c, SKRect r, int row)
{
    var cols = row % 2 == 0
        ? new[] { new SKColor(0x9C, 0xC4, 0xE8), new SKColor(0xD8, 0xE6, 0xF0), new SKColor(0x6E, 0x7A, 0x6A) }
        : new[] { new SKColor(0x2A, 0x33, 0x2C), new SKColor(0x3A, 0x3E, 0x44), new SKColor(0x23, 0x25, 0x29) };
    using var sh = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom), cols, new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp);
    using var p = new SKPaint { Shader = sh };
    c.DrawRect(r, p);
}

void Save(SKSurface s, string path) { using var img = s.Snapshot(); using var d = img.Encode(SKEncodedImageFormat.Png, 90); File.WriteAllBytes(path, d.ToArray()); }

foreach (var t in tracks)
{
    if (only.Count > 0 && !only.Contains(t.Slug)) continue;
    string dir = Path.Combine(outRoot, t.Slug);
    Directory.CreateDirectory(dir);
    var stops = TrackCorners.Lap(t.Raw, t.Len) ?? new();
    var log = new List<string> { $"{t.Display}  ({t.Raw})", "story: " + (TrackStory.Description(t.Raw) ?? "(none)"), "first race: " + (TrackStory.FirstRace(t.Raw) ?? "(none)"),
                                 "broadcast name: " + (TrackNames.ResolveInfo(t.Raw)?.Name ?? TrackNames.Resolve(t.Raw)) + "  flag: " + (TrackNames.ResolveInfo(t.Raw)?.CountryCode ?? "-"), "" };
    foreach (var st in stops)
        log.Add($"{(st.Straight ? "STRAIGHT" : "CORNER  ")} {st.StartPct:0.000}-{st.EndPct:0.000}  {st.Name,-28} theme={CornerTheme.For(st.Name.ToUpperInvariant())?.Kind.ToString() ?? "plain"}  heritage={CornerTheme.HeritageOf(t.Raw, st.Name) ?? "-"}");
    File.WriteAllLines(Path.Combine(dir, "stops.txt"), log);
    Console.WriteLine(string.Join("\n", log));

    // 1. every stop as the onboard label, two columns
    const float CW = 1000f, CH = 230f, Head = 70f;
    int rows = (stops.Count + 1) / 2;
    using (var surf = SKSurface.Create(new SKImageInfo((int)(2 * CW), (int)(Head + rows * CH))))
    {
        var c = surf.Canvas;
        c.Clear(new SKColor(0x0E, 0x10, 0x14));
        using var title = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 22f, Color = SKColors.White };
        using var small = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 13f, Color = new SKColor(0xB8, 0xBE, 0xC8) };
        c.DrawText($"{t.Display.ToUpperInvariant()}  -  {stops.Count} named stops in lap order (onboard label + heritage line)", 20, 32, title);
        c.DrawText("Placeholder backgrounds. Brand themes need Angelo's corner_logos pictures (fallback shown here).", 20, 56, small);
        for (int i = 0; i < stops.Count; i++)
        {
            var st = stops[i];
            var cell = new SKRect(i % 2 * CW, Head + i / 2 * CH, (i % 2 + 1) * CW, Head + (i / 2 + 1) * CH);
            Bg(c, cell, i / 2 + i % 2);
            c.Save(); c.ClipRect(cell);
            string nm = st.Name.ToUpperInvariant();
            CornerTheme.DrawLabel(c, cell.MidX, cell.Top + 70, st.Straight ? "DOWN" : "INTO", nm, 1f, CornerTheme.HeritageOf(t.Raw, st.Name));
            c.Restore();
            using var tag = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 12f, Color = new SKColor(0, 0, 0, 170) };
            c.DrawText($"{i + 1}. {CornerTheme.For(nm)?.Kind.ToString() ?? "plain"}  @ {st.StartPct:0.000}", cell.Left + 8, cell.Bottom - 8, tag);
        }
        Save(surf, Path.Combine(dir, "corner_labels.png"));
    }

    // 2. the track card (TrackFacts): story + facts before the walk, then a few stops of the lap walk
    var sn = RaceSnapshot.Disconnected with { IsConnected = true, Phase = RacePhase.PreRace, TrackDisplayName = t.Display, TrackRawName = t.Raw, TrackBroadcastName = t.Display, TrackCountryCode = t.Cc,
        Extras = RaceSnapshot.Disconnected.Extras with { TrackLength = t.Len, TrackCity = t.City, TrackCountry = t.Country, TrackTurns = t.Turns } };
    StartingGridRenderer.CardsStartMs = 0;
    var times = new List<double> { 5.0 };
    foreach (int k in new[] { 0, stops.Count / 3, 2 * stops.Count / 3, stops.Count - 1 }.Distinct().Where(k => k >= 0))
        times.Add(0.45 + 7.8 + k * 7.2 + 3.6);
    var frames = new List<SKImage>();
    foreach (double ts in times)
    {
        var pc = new PreRaceCards { Screen = new SKRect(0, 0, 1920, 1080) };
        using var surf = SKSurface.Create(new SKImageInfo(1920, 1080));
        var c = surf.Canvas;
        for (double tt = 0; tt <= ts + 1e-6; tt += 0.5) { c.Clear(new SKColor(70, 80, 90)); pc.Draw(c, 1920 - 1000, 860, sn, (tt + 0.6) * 1000); }
        Save(surf, Path.Combine(dir, $"track_card_{ts:0.0}s.png"));
        frames.Add(surf.Snapshot());
    }
    // contact sheet of the track card frames, half size, two across
    int fr = (frames.Count + 1) / 2;
    using (var sheet = SKSurface.Create(new SKImageInfo(1920, fr * 540 + 50)))
    {
        var c = sheet.Canvas;
        c.Clear(new SKColor(0x0E, 0x10, 0x14));
        using var title = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 20f, Color = SKColors.White };
        c.DrawText($"{t.Display.ToUpperInvariant()}  -  track card (story, facts, lap walk) at {string.Join(", ", times.Select(x => x.ToString("0.0") + " s"))}", 16, 32, title);
        using var fp = new SKPaint { FilterQuality = SKFilterQuality.High };
        for (int i = 0; i < frames.Count; i++)
            c.DrawImage(frames[i], new SKRect(i % 2 * 960, 50 + i / 2 * 540, (i % 2 + 1) * 960, 50 + (i / 2 + 1) * 540), fp);
        Save(sheet, Path.Combine(dir, "track_card_contact.png"));
    }
    foreach (var f in frames) f.Dispose();
}
