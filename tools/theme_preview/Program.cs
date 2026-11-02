// #601 (2026-10-30): broadcast themes - previews, the Default pixel check and the per-frame theme bench, drawn by the REAL
// renderers over a procedural trackside picture (make_bg.py). Drivers are the made-up RaceSim field.
//   theme_preview stills OUT_DIR BG.png [--themes a,b] [--custom file.json]   one 4K still per theme (+ custom themes)
//   theme_preview video  OUT.mp4 BG.png THEME [seconds] [fps]                 a 4K clip showing the motion (needs ffmpeg)
//   theme_preview diff   OUT_DIR                                              Default: per-graphic pixel hashes (+ PNGs)
//   theme_preview specs                                                       the built-in presets as JSON
//   theme_preview bench  THEME [frames] [--nodraw] [--custom file.json]       CPU ms + managed bytes per frame, per graphic
//   theme_preview rcbar|latest OUT_DIR BG.png, rcclip|camsim OUT.mp4 BG.png, camsbench   #608 / #611 / #619 (Cams.cs)
// "diff" only uses what the app had before #601, so the same source builds against the old assemblies (-p:OldBuild=1)
// and the hashes of both builds can be compared line by line.
using System.Diagnostics;
using System.Reflection;
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

string mode = args.Length > 0 ? args[0] : "stills";
string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
StorySound.Enabled = false;
OverlayLog.Sink = _ => { };
Fonts.WarmUp();
var snap = RaceSim.Run();
StoryDirector.CardMs = 600000;   // the story card holds for the whole preview

switch (mode)
{
    case "diff": Diff(args[1]); break;
#if !OLDBUILD
    case "stills": Stills(args[1], args[2]); break;
    case "video": Video(args[1], args[2], args[3], args.Length > 4 ? double.Parse(args[4]) : 10, args.Length > 5 ? int.Parse(args[5]) : 30); break;
    case "specs": Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(Theme.BuiltIns.Select(t => t.ToSpec()),
        new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, WriteIndented = true })); break;
    case "bench": Bench(args[1], args.Length > 2 && int.TryParse(args[2], out int fr) ? fr : 300, args.Contains("--nodraw")); break;
    case "rcbar": CamsPreview.RcStills(args[1], args[2], snap); break;          // #619
    case "latest": CamsPreview.LatestStills(args[1], args[2], snap); break;     // #608
    case "rcclip": CamsPreview.RcClip(args[1], args[2], snap); break;           // #619
    case "camsim": CamsPreview.CamSim(args[1], args[2], snap); break;           // #611
    case "camsbench": CamsPreview.Bench(snap); break;                          // #608 / #611 / #619
#endif
    default: Console.WriteLine("modes: stills / video / diff / bench"); break;
}

// ---------------- the Default pixel check ----------------
void Diff(string outDir)
{
    Directory.CreateDirectory(outDir);
    Theme.Set("default");
    var sc = new Scene(snap);
    using var surf = SKSurface.Create(new SKImageInfo(3840, 2160, SKColorType.Rgba8888, SKAlphaType.Premul));
    var c = surf.Canvas;
    var lines = new List<string>();
    // play the scene forward on a no-draw canvas, then hash each graphic alone at a few moments
    using var nd = new SKNoDrawCanvas(1920, 1080);
    double[] moments = { 6000, 9300, 12700 };
    double t = 0;
    foreach (var m in moments)
    {
        for (; t < m; t += 16.7) foreach (var p in Scene.Parts) sc.Frame(nd, t, p);
        foreach (var part in Scene.Parts)
        {
            c.Clear(SKColors.Transparent);
            c.Save(); c.Scale(2f);
            sc.Frame(c, t, part);
            c.Restore(); c.Flush();
            using var pm = surf.PeekPixels();
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pm.GetPixelSpan()))[..16];
            lines.Add($"t={m,6} {part,-10} {hash}");
            if (m == moments[1]) { using var img = surf.Snapshot(); using var d = img.Encode(SKEncodedImageFormat.Png, 100); File.WriteAllBytes(Path.Combine(outDir, part + ".png"), d.ToArray()); }
        }
    }
    File.WriteAllLines(Path.Combine(outDir, "hashes.txt"), lines);
    foreach (var l in lines) Console.WriteLine(l);
}

#if !OLDBUILD
List<ThemeDef> Customs()
{
    var path = Arg("--custom");
    if (path == null) return new();
    var specs = System.Text.Json.JsonSerializer.Deserialize<List<ThemeSpec>>(File.ReadAllText(path),
        new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }) ?? new();
    Theme.SetCustoms(specs);
    return specs.Select(s => Theme.ById(s.Id)).ToList();
}

void UseTheme(string id) { Customs(); Theme.Set(id); }

void Stills(string outDir, string bgPath)
{
    Directory.CreateDirectory(outDir);
    File.Delete(Path.Combine(outDir, "names.txt"));
    var customs = Customs();
    var ids = Arg("--themes")?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
              ?? Theme.BuiltIns.Select(t => t.Id).Concat(customs.Select(t => t.Id)).Distinct().ToList();
    using var bg = SKImage.FromEncodedData(bgPath);
    using var surf = SKSurface.Create(new SKImageInfo(3840, 2160, SKColorType.Rgba8888, SKAlphaType.Premul));
    var c = surf.Canvas;
    int n = 1;
    foreach (var id in ids)
    {
        Theme.Set(id);
        var th = Theme.Current;
        var sc = new Scene(snap);
        using var nd = new SKNoDrawCanvas(1920, 1080);
        double t = 0;
        for (; t < 9300; t += 16.7) sc.Frame(nd, t, Scene.All);
        // GS_DRAW_TWICE (#604): the last frames on the real 4K canvas (cleared between), so pictures kept between frames
        // (ScreenPlate - made only after two identical frames) are the ones in the still
        if (Environment.GetEnvironmentVariable("GS_DRAW_TWICE") != null)
            for (int k = 2; k >= 1; k--)
            {
                c.Clear(SKColors.Transparent); c.Save(); c.Scale(2f); sc.Frame(c, t - 16.7 * k, Scene.All); c.Restore(); c.Flush();
            }
        c.Clear(SKColors.Transparent);
        c.DrawImage(bg, new SKRect(0, 0, 3840, 2160));
        c.Save(); c.Scale(2f);
        sc.Frame(c, t, Scene.All);
        c.Restore(); c.Flush();
        using var img = surf.Snapshot(); using var d = img.Encode(SKEncodedImageFormat.Png, 100);
        string file = Path.Combine(outDir, $"{n++:00}_{th.Id}.png");
        File.WriteAllBytes(file, d.ToArray());
        File.AppendAllText(Path.Combine(outDir, "names.txt"), $"{Path.GetFileName(file)}\t{th.Name}{(th.BuiltIn ? "" : " (custom example)")}\n");
        Console.WriteLine($"ok {file}  ({th.Name})");
    }
}

void Video(string outFile, string bgPath, string id, double seconds, int fps)
{
    UseTheme(id);
    using var bg = SKImage.FromEncodedData(bgPath);
    var sc = new Scene(snap);
    using var nd = new SKNoDrawCanvas(1920, 1080);
    double t = 0;
    for (; t < 8000; t += 16.7) sc.Frame(nd, t, Scene.All);
    var info = new SKImageInfo(3840, 2160, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var surf = SKSurface.Create(info);
    var c = surf.Canvas;
    var psi = new ProcessStartInfo("ffmpeg", $"-y -loglevel error -f rawvideo -pix_fmt rgba -s 3840x2160 -r {fps} -i - -c:v libx264 -preset medium -crf 20 -pix_fmt yuv420p -movflags +faststart \"{outFile}\"")
    { RedirectStandardInput = true, UseShellExecute = false };
    using var ff = Process.Start(psi)!;
    var stdin = ff.StandardInput.BaseStream;
    int frames = (int)(seconds * fps);
    double step = 1000.0 / fps;
    var buf = new byte[info.BytesSize];
    for (int f = 0; f < frames; f++, t += step)
    {
        // the scene's own state machines advance at 60 Hz between the recorded frames
        for (double q = t - step + 16.7; q < t; q += 16.7) sc.Frame(nd, q, Scene.All);
        c.DrawImage(bg, new SKRect(0, 0, 3840, 2160));
        c.Save(); c.Scale(2f);
        sc.Frame(c, t, Scene.All);
        c.Restore(); c.Flush();
        using var pm = surf.PeekPixels();
        pm.GetPixelSpan().CopyTo(buf);
        stdin.Write(buf, 0, buf.Length);
    }
    stdin.Close();
    ff.WaitForExit();
    Console.WriteLine($"ok {outFile} ({frames} frames, {Theme.Current.Name})");
}

void Bench(string id, int frames, bool noDraw)
{
    UseTheme(id);
    var sc = new Scene(snap);
    var info = new SKImageInfo(3840, 2160, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var surf = SKSurface.Create(info);
    SKCanvas c = noDraw ? new SKNoDrawCanvas(3840, 2160) : surf.Canvas;
    var parts = Scene.Parts;
    var ms = new double[parts.Length]; var bytes = new long[parts.Length];
    double t = 0; int warm = 360;
    for (int f = 0; f < frames + warm; f++, t += 16.7)
    {
        bool rec = f >= warm;
        c.Clear(SKColors.Transparent);
        c.Save(); c.Scale(2f);
        for (int i = 0; i < parts.Length; i++)
        {
            long a0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Stopwatch.GetTimestamp();
            sc.Frame(c, t, parts[i], tickOnly: i > 0);
            if (rec) { ms[i] += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency; bytes[i] += GC.GetAllocatedBytesForCurrentThread() - a0; }
        }
        c.Restore(); c.Flush();
    }
    Console.WriteLine($"bench {Theme.Current.Name}: {frames} frames at 3840x2160 ({(noDraw ? "no-draw = CPU side only" : "CPU raster")})");
    double tot = 0; long totB = 0;
    for (int i = 0; i < parts.Length; i++)
    {
        Console.WriteLine($"  {parts[i],-10} {ms[i] / frames,7:F3} ms/frame  {bytes[i] / (double)frames,9:F1} B/frame");
        tot += ms[i]; totB += bytes[i];
    }
    Console.WriteLine($"  {"TOTAL",-10} {tot / frames,7:F3} ms/frame  {totB / (double)frames,9:F1} B/frame");
}
#endif

/// <summary>A race frame laid out the way OverlayCompositor lays it out (1920x1080 design space): the tower, the session
/// bar + watch, the driver card (bottom centre) and a story card (right third); plus, for the pixel check, the battle card,
/// the standings ticker, a team radio card and the fastest-lap callout (each drawn alone).</summary>
sealed class Scene
{
    public const string All = "all";
    public static readonly string[] Parts = { "tower", "sessionbar", "driver", "story", "battle", "ticker", "radio", "fastest" };
    readonly RaceSnapshot snap, battleSnap, fastSnap;
    readonly TimingTowerRenderer tower = new();
    readonly SessionBarRenderer bar = new();
    readonly WatchRenderer watch = new();
    readonly FeaturedCardRenderer featured = new();
    readonly StoryCards story = new();
    readonly BattleCardRenderer battle = new();
    readonly StandingsTickerRenderer ticker = new();
    readonly RadioPopupRenderer radio = new();
    readonly FastestLapRenderer fastest = new();
    static readonly PropertyInfo storyCurrent = typeof(StoryDirector).GetProperty("Current")!;
    readonly DateTime clock0 = new(2026, 10, 30, 20, 41, 0);
    DateTime clock;
    bool storyForced, radioPushed;

    public Scene(RaceSnapshot s)
    {
        snap = s;
        clock = clock0;
        SessionBarRenderer.TestClock = () => clock;
        // a battle for the player: the car ahead 0.5 s up the road, the car behind 0.6 s back (as tools/visual_pack)
        var g = s.ClassLeaderboard[0].Drivers;
        int me = g.ToList().FindIndex(d => d.CarIdx == s.PlayerCarIdx);
        var list = new List<Driver>();
        for (int i = 0; i < g.Count; i++)
            list.Add(g[i] with { LapDistPct = 0.42f - i * 0.0025f, IntervalSeconds = i == 0 ? 0 : i == me ? 0.5 : i == me + 1 ? 0.6 : 1.4 });
        var cls = new List<DriverClass> { new(s.ClassLeaderboard[0].Name, s.ClassLeaderboard[0].Color, list) };
        cls.AddRange(s.ClassLeaderboard.Skip(1));
        battleSnap = s with { ClassLeaderboard = cls, Player = list[me], CameraTarget = list[me] };
        // a new fastest lap for the class leader
        float best = s.ClassLeaderboard.SelectMany(c => c.Drivers).Where(d => d.BestLapTime > 0).Min(d => d.BestLapTime);
        var cl = s.ClassLeaderboard.Select(c => c with { Drivers = c.Drivers.Select((d, i) => i == 0 && c == s.ClassLeaderboard[0] ? d with { BestLapTime = best - 0.9f } : d).ToList() }).ToList();
        fastSnap = s with { ClassLeaderboard = cl };
    }

    /// <summary>Draws one graphic (or <see cref="All"/>) at time t (ms). Graphics not drawn this frame still advance on a
    /// no-draw canvas so every state machine keeps the same clock.</summary>
    public void Frame(SKCanvas c, double t, string part, bool tickOnly = false)
    {
        clock = clock0.AddMilliseconds(t);
#if !OLDBUILD
        Theme.Clock = t;
#endif
        PresentationState.IsBattleActive = false;
        PresentationState.Tick(snap);
        bool all = part == All;
        if (all || part == "tower") tower.Draw(c, 16f, 18f, 1080 - 34f, snap, t);
        if (all || part == "story")
        {
            if (!storyForced && t >= 2000) { storyForced = true; StoryDirector.Force(StoryKind.RaceSoFar, t); }
            story.Draw(c, 1920, 1080, snap, t);
        }
        // driver card (in the app it steps aside while a story card is up; here we want both, so it is drawn with the
        // story card hidden for a moment)
        if (all || part == "driver")
        {
            var keep = StoryDirector.Current;
            if (keep != StoryKind.None) storyCurrent.SetValue(null, StoryKind.None);
            c.Save(); c.Translate((1920 - 768f) / 2f, 1080 - 16f - 211f);
            featured.Draw(c, 24f, 32.5f, snap, t);
            c.Restore();
            if (keep != StoryKind.None) storyCurrent.SetValue(null, keep);
        }
        if (part == "battle")
        {
            PresentationState.IsBattleActive = true;
            battle.Draw(c, (1920 - 1280f) / 2f + 140f, 1080 - 348f + 30f, battleSnap, t);
        }
        if (part == "ticker") { PresentationState.IsBattleActive = true; ticker.Draw(c, 0f, 110f, 1920, snap, t); }
        if (part == "radio")
        {
            if (!radioPushed) { radioPushed = true; RadioFeed.Push("ENGINEER", "Box this lap, box this lap. Fuel only, tyres are fine."); }
            radio.Draw(c, 1920 - 24f, 1080 * 0.665f, snap, t);
        }
        if (part == "fastest") fastest.Draw(c, (1920 - 720f) / 2f, 580f, t < 5000 ? snap : fastSnap, t);
        if (all || part == "sessionbar")
        {
            float bw = SessionBarRenderer.BarWidth, bx = (1920 - bw) / 2f;
            bar.Draw(c, bx, 18f, 1f, snap, clock, t, bw);
            watch.Draw(c, bx + bw / 2f, 18f + SessionBarRenderer.BarHeight / 2f, 64f, clock);
        }
    }
}
