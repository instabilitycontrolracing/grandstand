using System.IO;
using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// The globe intro (2026-10-20, Angelo: "a crazy stylised globe that zooms in to the real track
/// location"). A pre-race panel that plays right before the track card: a glowing dot globe in
/// space (city lights, borders, an orbit ring) spins round to the circuit's real coordinates,
/// locks on, dives through rivers and motorways to the town, drops a pin, and draws the circuit.
///
/// <para>Speed (Angelo: "none of this should render in real time or it gets clunky"): everything
/// heavy is done ONCE, off the render thread, and kept:
///   - the world map (Natural Earth, public domain) ships as a small binary pack in assets\geo
///     and is loaded once in the background;
///   - each circuit's close-up detail (three levels of land dots tested against the coastline,
///     plus its rivers, motorways and nearby towns) is built in the background the moment the
///     track is known, and saved to %LOCALAPPDATA%\ApexHUD\globe so the next visit loads instantly;
///   - glows and stars are pre-made pictures.
/// What's left per frame is placing those prepared dots and lines - light work for the graphics
/// card. If a track's detail isn't ready yet, the panel simply isn't shown that time.</para>
///
/// <para>Track coordinates come straight from iRacing (WeekendInfo TrackLatitude/Longitude), so it
/// works at every track with no setup.</para>
/// </summary>
public static class GlobeIntro
{
    public const double Length = 8.6;      // seconds the flight takes inside its panel

    static readonly SKColor Cyan = new(0x8F, 0xE3, 0xFF), Navy0 = new(0x0B, 0x1C, 0x36), Navy1 = new(0x03, 0x08, 0x14);
    static readonly SKColor Red = new(0xD8, 0x1E, 0x2A), Amber = new(0xFF, 0xC8, 0x7A), Warm = new(0xFF, 0xF2, 0xD0), River = new(0x4F, 0xB3, 0xFF);

    // ------------------------------------------------------------------ world pack (shared)
    sealed class Lines
    {
        public readonly List<float[]> Items = new();   // each: lat0,lon0,lat1,lon1,...
    }
    sealed record City(float Lat, float Lon, float Pop, string Name)
    {
        public string Upper { get; } = Name.ToUpperInvariant();   // town names are drawn in capitals (worked out once)
    }
    sealed class World
    {
        public Lines Land50 = new(), Land10 = new(), Borders = new(), Rivers = new(), Roads = new();
        public float[] Dots0 = Array.Empty<float>();
        public List<City> Cities = new();
    }
    static World? world;
    static Task? worldTask;

    static Lines ReadLines(string file)
    {
        var l = new Lines();
        var path = Assets.Resolve("geo", file);
        if (!File.Exists(path)) return l;
        using var br = new BinaryReader(File.OpenRead(path));
        int n = br.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            int m = br.ReadInt32();
            var a = new float[m * 2];
            for (int k = 0; k < a.Length; k++) a[k] = br.ReadSingle();
            l.Items.Add(a);
        }
        return l;
    }

    static World LoadWorld()
    {
        var w = new World
        {
            Land50 = ReadLines("land50.bin"), Land10 = ReadLines("land10.bin"), Borders = ReadLines("borders50.bin"),
            Rivers = ReadLines("rivers10.bin"), Roads = ReadLines("roads10.bin"),
        };
        var dp = Assets.Resolve("geo", "dots0.bin");
        if (File.Exists(dp))
        {
            using var br = new BinaryReader(File.OpenRead(dp));
            int n = br.ReadInt32(); w.Dots0 = new float[n * 2];
            for (int k = 0; k < w.Dots0.Length; k++) w.Dots0[k] = br.ReadSingle();
        }
        var cp = Assets.Resolve("geo", "cities.bin");
        if (File.Exists(cp))
        {
            using var br = new BinaryReader(File.OpenRead(cp));
            int n = br.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                float la = br.ReadSingle(), lo = br.ReadSingle(), pop = br.ReadSingle();
                int len = br.ReadByte();
                string name = System.Text.Encoding.UTF8.GetString(br.ReadBytes(len));
                w.Cities.Add(new City(la, lo, pop, name));
            }
        }
        return w;
    }

    // ------------------------------------------------------------------ per-track scene
    /// <summary>One set of land dots, with everything a frame needs precomputed.</summary>
    sealed class DotLevel
    {
        public int N;
        public float[] Lat = Array.Empty<float>(), Lon = Array.Empty<float>();
        public float[] SinLat = Array.Empty<float>(), CosLat = Array.Empty<float>(), LonR = Array.Empty<float>();
        public float[] Shade = Array.Empty<float>();   // edge fade x terrain shimmer, precomputed
        public float Step;                               // spacing in radians
        public float HeatScale;
        public void Finish(double lat0, double lon0, float half, float terrainF)
        {
            N = Lat.Length;
            SinLat = new float[N]; CosLat = new float[N]; LonR = new float[N]; Shade = new float[N];
            for (int i = 0; i < N; i++)
            {
                double la = Lat[i] * Math.PI / 180, lo = Lon[i] * Math.PI / 180;
                SinLat[i] = (float)Math.Sin(la); CosLat[i] = (float)Math.Cos(la); LonR[i] = (float)lo;
                double a = Lat[i] * terrainF, b = Lon[i] * terrainF;
                double ter = 0.5 + 0.25 * Math.Sin(a * 1.7 + Math.Sin(b * 0.9)) + 0.25 * Math.Sin(b * 1.3 + 2 * Math.Cos(a * 0.6));
                double edge = 1;
                if (half > 0)
                {
                    double n = Math.Max(Math.Abs(Lat[i] - lat0) / half, Math.Abs(Lon[i] - lon0) / (half * 1.5));
                    edge = 1 - Math.Pow(Math.Clamp((n - 0.35) / 0.6, 0, 1), 1.2);
                }
                Shade[i] = (float)(edge * (0.55 + 0.45 * ter));
            }
        }
    }

    sealed class Scene
    {
        public string Key = "";
        public double Lat, Lon;
        public DotLevel[] Levels = Array.Empty<DotLevel>();
        public List<float[]> Coast = new(), Borders = new(), Rivers = new(), Roads = new();
        public List<City> Cities = new();
        public List<City> Labels = new();
        public SKPoint[]? Track;
    }
    static Scene? scene;
    static string? building;

    /// <summary>Cheap, call every frame while pre-race: starts the background build for this track
    /// the first time its coordinates are known.</summary>
    static double seenLat = double.NaN, seenLon = double.NaN; static string? seenRaw;
    public static void Prepare(RaceSnapshot s)
    {
        GlobeGT.Prepare(s.TrackLatitude, s.TrackLongitude);   // the GT7-style globe (2026-10-21)
        if (double.IsNaN(s.TrackLatitude) || double.IsNaN(s.TrackLongitude)) return;
        if (s.TrackLatitude == seenLat && s.TrackLongitude == seenLon && ReferenceEquals(s.TrackRawName, seenRaw) && (scene != null || building != null)) return;
        seenLat = s.TrackLatitude; seenLon = s.TrackLongitude; seenRaw = s.TrackRawName;
        string key = KeyFor(s);
        if (scene?.Key == key || building == key) return;
        building = key;
        worldTask ??= Task.Run(() => { try { world = LoadWorld(); } catch { world = new World(); } });
        StartBuild(key, s);
    }

    // a separate method so the background job's closure is only allocated when a build starts (2026-10-24:
    // captured locals in Prepare made it allocate on every call, i.e. every frame)
    static void StartBuild(string key, RaceSnapshot s)
    {
        string city = s.Extras.TrackCity ?? "";
        var trackPts = LoadTrack(s);
        double lat = s.TrackLatitude, lon = s.TrackLongitude;
        Task.Run(async () =>
        {
            try
            {
                await worldTask;
                var sc = BuildScene(key, lat, lon, city, trackPts);
                PrepareSpeedLines();
                scene = sc;
            }
            catch { }
            finally { if (building == key) building = null; }
        });
    }

    /// <summary>True when this track's globe is prepared and can play.</summary>
    public static bool Ready(RaceSnapshot s) => GlobeGT.Ready(s) || (scene != null && !double.IsNaN(s.TrackLatitude) && scene.Key == KeyMemo(s));

    // the scene key, made only when the track's name / coordinates change (asked every frame)
    static string? memoRaw; static double memoLat = double.NaN, memoLon = double.NaN; static string memoKey = "";
    static string KeyMemo(RaceSnapshot s)
    {
        if (s.TrackLatitude != memoLat || s.TrackLongitude != memoLon || s.TrackRawName != memoRaw)
        { memoKey = KeyFor(s); memoLat = s.TrackLatitude; memoLon = s.TrackLongitude; memoRaw = s.TrackRawName; }
        return memoKey;
    }

    static string KeyFor(RaceSnapshot s) =>
        string.Format(CultureInfo.InvariantCulture, "{0}_{1:0.0000}_{2:0.0000}", (s.TrackRawName ?? "track").Replace(' ', '_'), s.TrackLatitude, s.TrackLongitude);

    static SKPoint[]? LoadTrack(RaceSnapshot s)
    {
        try
        {
            var key = TrackMapCache.Resolve(s.TrackConfigName, s.TrackDisplayName, s.TrackBroadcastName, s.TrackRawName);
            var json = key == null ? null : Assets.Resolve("maps", key + ".json");
            if (json == null || !File.Exists(json)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
            var list = new List<SKPoint>();
            foreach (var pt in doc.RootElement.GetProperty("points").EnumerateArray()) list.Add(new SKPoint(pt[0].GetSingle(), pt[1].GetSingle()));
            return list.Count >= 20 ? list.ToArray() : null;
        }
        catch { return null; }
    }

    static readonly string GlobeDir = ApexHUD.Native.Common.TrackMapStore.MigrateFolder(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApexHUD", "globe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Grandstand", "globe"));

    static string CachePath(string key)
    {
        var dir = GlobeDir;   // #48 stage 2: LocalAppData\Grandstand\globe (the old ApexHUD\globe copied across once)
        foreach (var ch in Path.GetInvalidFileNameChars()) key = key.Replace(ch, '_');
        return Path.Combine(dir, key + ".v1.bin");
    }

    // the three close-up dot grids: (half-size in degrees, spacing in degrees, use 10m coastline)
    static readonly (float Half, float Step, bool Fine)[] Grids = { (16f, 0.16f, false), (2.6f, 0.026f, true), (0.75f, 0.0042f, true) };

    static Scene BuildScene(string key, double lat0, double lon0, string city, SKPoint[]? track)
    {
        var w = world ?? new World();
        var sc = new Scene { Key = key, Lat = lat0, Lon = lon0, Track = track };
        var levels = new DotLevel[4];
        // level 0: the whole planet (shipped precomputed)
        var l0 = new DotLevel { Step = (float)Math.Sqrt(4 * Math.PI / 100000), HeatScale = 6 };
        int n0 = w.Dots0.Length / 2; l0.Lat = new float[n0]; l0.Lon = new float[n0];
        for (int i = 0; i < n0; i++) { l0.Lat[i] = w.Dots0[2 * i]; l0.Lon[i] = w.Dots0[2 * i + 1]; }
        l0.Finish(lat0, lon0, 0, 0.3f);
        levels[0] = l0;
        // levels 1-3: grids around the circuit, kept only where there's land. Loaded from the disk
        // cache when this track has been seen before; otherwise built (coastline drawn into a mask
        // at exactly the grid's resolution - fast) and saved.
        var cache = CachePath(key);
        bool loaded = false;
        try
        {
            if (File.Exists(cache))
            {
                using var br = new BinaryReader(File.OpenRead(cache));
                for (int k = 1; k <= 3; k++)
                {
                    int n = br.ReadInt32();
                    var d = new DotLevel { Lat = new float[n], Lon = new float[n] };
                    for (int i = 0; i < n; i++) { d.Lat[i] = br.ReadSingle(); d.Lon[i] = br.ReadSingle(); }
                    levels[k] = d;
                }
                loaded = true;
            }
        }
        catch { loaded = false; }
        if (!loaded)
        {
            for (int k = 1; k <= 3; k++)
            {
                var (half, step, fine) = Grids[k - 1];
                levels[k] = LandGrid(fine ? w.Land10 : w.Land50, lat0, lon0, half, step);
            }
            try
            {
                using var bw = new BinaryWriter(File.Create(cache));
                for (int k = 1; k <= 3; k++)
                {
                    bw.Write(levels[k].Lat.Length);
                    for (int i = 0; i < levels[k].Lat.Length; i++) { bw.Write(levels[k].Lat[i]); bw.Write(levels[k].Lon[i]); }
                }
            }
            catch { }
        }
        float[] terrainF = { 0.3f, 1.6f, 9f, 55f }, heat = { 6f, 1.2f, 0.25f, 0.04f };
        for (int k = 1; k <= 3; k++)
        {
            levels[k].Step = (float)(Grids[k - 1].Step * Math.PI / 180);
            levels[k].HeatScale = heat[k];
            levels[k].Finish(lat0, lon0, Grids[k - 1].Half, terrainF[k]);
        }
        sc.Levels = levels;
        // lines: coast (50m outlines) and borders everywhere; rivers / roads near the circuit only
        sc.Coast = w.Land50.Items.Where(a => a.Length >= 16).ToList();
        sc.Borders = w.Borders.Items;
        sc.Rivers = w.Rivers.Items.Where(a => Near(a, lat0, lon0, 18)).ToList();
        sc.Roads = w.Roads.Items.Where(a => Near(a, lat0, lon0, 5)).ToList();
        sc.Cities = w.Cities.Where(c => c.Pop > 250000 || (Math.Abs(c.Lat - lat0) < 4 && Math.Abs(c.Lon - lon0) < 6)).ToList();
        sc.Labels = w.Cities.Where(c => Math.Abs(c.Lat - lat0) < 4 && Math.Abs(c.Lon - lon0) < 6 && c.Pop >= 100000
                                        && !string.Equals(c.Name, city, StringComparison.OrdinalIgnoreCase)
                                        && Math.Abs(c.Lat - lat0) + Math.Abs(c.Lon - lon0) > 0.25)
                            .OrderByDescending(c => c.Pop).Take(14).ToList();
        return sc;
    }

    static bool Near(float[] a, double lat0, double lon0, double box)
    {
        for (int i = 0; i < a.Length; i += 2)
            if (Math.Abs(a[i] - lat0) < box && Math.Abs(a[i + 1] - lon0) < box * 1.5) return true;
        return false;
    }

    /// <summary>A lat/lon grid around the circuit, kept where the coastline says there's land.</summary>
    static DotLevel LandGrid(Lines land, double lat0, double lon0, float half, float step)
    {
        double latMin = lat0 - half, lonMin = lon0 - half * 1.5;
        int rows = (int)(2 * half / step), cols = (int)(2 * half * 1.5 / step);
        using var mask = new SKBitmap(new SKImageInfo(cols, rows, SKColorType.Alpha8, SKAlphaType.Premul));
        using (var cv = new SKCanvas(mask))
        {
            cv.Clear(SKColors.Transparent);
            // grid cell (col,row) has its centre at pixel centre; lat increases upward -> row = (latMax - lat) / step
            double latMax = latMin + rows * step;
            using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            foreach (var a in land.Items)
            {
                bool any = false;
                for (int i = 0; i < a.Length; i += 2)
                    if (a[i] > latMin - 1 && a[i] < latMax + 1 && a[i + 1] > lonMin - 1 && a[i + 1] < lonMin + cols * step + 1) { any = true; break; }
                if (!any) continue;
                path.MoveTo((float)((a[1] - lonMin) / step - 0.5), (float)((latMax - a[0]) / step - 0.5));
                for (int i = 2; i < a.Length; i += 2) path.LineTo((float)((a[i + 1] - lonMin) / step - 0.5), (float)((latMax - a[i]) / step - 0.5));
                path.Close();
            }
            using var p = new SKPaint { Color = SKColors.White, IsAntialias = false };
            cv.DrawPath(path, p);
        }
        var la = new List<float>(); var lo = new List<float>();
        var px = mask.Bytes;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                if (px[r * mask.RowBytes + c] > 127)
                {
                    la.Add((float)(latMin + (rows - 1 - r) * step + step * 0.0));
                    lo.Add((float)(lonMin + (c + 1) * step));
                }
        return new DotLevel { Lat = la.ToArray(), Lon = lo.ToArray() };
    }

    // ------------------------------------------------------------------ prepared pictures
    static SKImage? glow, starsImg;
    static SKImage Glow()
    {
        if (glow != null) return glow;
        using var surf = SKSurface.Create(new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var sh = SKShader.CreateRadialGradient(new SKPoint(32, 32), 32, new[] { Amber.WithAlpha(255), Amber.WithAlpha(90), Amber.WithAlpha(0) }, new[] { 0f, 0.35f, 1f }, SKShaderTileMode.Clamp);
        using var p = new SKPaint { Shader = sh, IsAntialias = true };
        surf.Canvas.Clear(SKColors.Transparent); surf.Canvas.DrawCircle(32, 32, 32, p);
        return glow = surf.Snapshot();
    }
    static readonly (float X, float Y, float R, float B)[] Stars = MakeStars();
    static (float, float, float, float)[] MakeStars()
    {
        var rnd = new Random(4); var s = new (float, float, float, float)[700];
        for (int i = 0; i < s.Length; i++) s[i] = ((float)rnd.NextDouble(), (float)rnd.NextDouble(), 0.3f + 1.1f * (float)rnd.NextDouble(), 0.2f + 0.8f * (float)rnd.NextDouble());
        return s;
    }

    // ------------------------------------------------------------------ drawing
    static float Clamp01(double x) => (float)Math.Clamp(x, 0, 1);
    static float Ease(double x) { float v = Clamp01(x); return v * v * (3 - 2 * v); }
    static float EaseIO(double x) { float v = Clamp01(x); return v < 0.5f ? 4 * v * v * v : 1 - (float)Math.Pow(-2 * v + 2, 3) / 2; }
    static float OutBack(double x) { float v = Clamp01(x); const float c1 = 1.7f, c3 = c1 + 1; return 1 + c3 * (float)Math.Pow(v - 1, 3) + c1 * (float)Math.Pow(v - 1, 2); }
    static float SStep(double e0, double e1, double x) => Ease((x - e0) / (e1 - e0));
    static SKColor A(SKColor c, double a) => c.WithAlpha((byte)(255 * Clamp01(a)));

    struct Cam { public double Lat, Lon, R, D, SinP, CosP, R0; public float Cx, Cy; }

    static Cam Camera(Scene sc, double t, SKRect r, bool opener = false, double tm = 0, bool splash = false)
    {
        double u = EaseIO((t - 0.2) / 2.8);
        double lon = sc.Lon + 125 * (1 - u) - 4 * (1 - u) * (1 - u);
        double lat = 12 + (sc.Lat - 12) * u;
        double d = opener ? Math.Pow(Clamp01((t - 3.0) / 3.3), 1.6) : EaseIO((t - 3.0) / 3.3);   // opener: accelerate INTO the music's hit
        double r0 = r.Width * (splash ? 0.205 : opener ? 0.2 : 0.335), r1 = r.Width * 0.8 / (0.118 * Math.PI / 180);
        double R = r0 * (1 + 0.06 * Ease(t / 3.0)) * Math.Exp(Math.Log(r1 / r0) * d);
        float cy = r.MidY - r.Height * 0.04f;
        if (splash)
        {
            // full-screen splash (2026-10-20): punches in big on the music hit, already turning
            // starts close in, already over Europe / Asia (Angelo 2026-10-20), and drifts onto the circuit
            double k = Ease(tm / (GridIntro.GlobeBarS * 0.5));
            lon = sc.Lon + 42 * (1 - k);
            lat = 40 + (sc.Lat - 40) * k;
            double zoom = 1.85 + 0.25 * k;                       // big from the first frame, easing closer
            R *= (1 + (zoom - 1) * (1 - d)) * (1.0 - 0.10 * (1 - Ease(tm / 0.5)));
        }
        else if (opener)
        {
            lon += 70 * (1 - Ease(tm / 4.4));                                           // slow spin under the iRacing logo
            cy += r.Height * (0.80f * (1 - EaseIO(tm / 3.8)) + 0.12f * (1 - EaseIO((tm - 4.4) / 1.6)));   // Earthrise, then up to centre
        }
        double p0 = lat * Math.PI / 180;
        return new Cam { Lat = lat, Lon = lon, R = R, D = d, R0 = r0, SinP = Math.Sin(p0), CosP = Math.Cos(p0), Cx = r.MidX, Cy = cy };
    }

    /// <summary>The broadcast opener's music clock -> the flight's own clock (2026-10-20): the globe
    /// spins under the iRacing logo, turns to the circuit under the series logo, locks on, and the
    /// dive lands exactly on the theme's track hit (GridIntro.TrackS).</summary>
    static double OpenerFlightTime(double tm)
    {
        double s = GridIntro.SeriesS, k = GridIntro.TrackS;   // 4.40, 8.00
        if (tm < s - 1.4) return 0.2 * tm / (s - 1.4);                                   // 0 .. 3.0  -> 0 .. 0.2
        if (tm < k - 1.8) return 0.2 + 2.8 * (tm - (s - 1.4)) / (k - 1.8 - (s - 1.4));     // 3.0 .. 6.2 -> 0.2 .. 3.0 (turn + lock on)
        if (tm < k) return 3.0 + 3.3 * (tm - (k - 1.8)) / 1.8;                            // 6.2 .. 8.0 -> 3.0 .. 6.3 (the dive)
        return 6.3;
    }

    /// <summary>The globe's own full-screen beat in the opener (2026-10-20, Angelo: original iRacing
    /// and series pages first, THEN the globe, THEN the track). <paramref name="u"/> = seconds into
    /// the beat (one bar of the theme, GridIntro.GlobeBarS): turn + lock on, then dive onto the track hit.</summary>
    public static void DrawSplash(SKCanvas cv, SKRect r, RaceSnapshot s, double u, float alpha)
    {
        if (GlobeGT.DrawSplash(cv, r, s, u, alpha)) return;   // GT7-style globe when its pictures are installed
        if (scene == null) return;
        double len = GridIntro.GlobeBarS, lockAt = len * 0.5;
        double ft = u < lockAt ? 0.7 + 2.3 * u / lockAt : 3.0 + 3.3 * Math.Min(1, (u - lockAt) / (len - lockAt));
        DrawCore(cv, r, s, ft, alpha, true, u, true);
    }

    /// <summary>Draws the globe as the broadcast opener's backdrop at music time <paramref name="tm"/>.</summary>
    public static void DrawOpener(SKCanvas cv, SKRect r, RaceSnapshot s, double tm, float alpha) =>
        DrawCore(cv, r, s, OpenerFlightTime(tm), alpha, true, tm);

    /// <summary>Project one lat/lon: returns false when it's on the far side of the globe.</summary>
    static bool Project(in Cam c, double lat, double lon, out float sx, out float sy, out float z)
    {
        double la = lat * Math.PI / 180, dl = (lon - c.Lon) * Math.PI / 180;
        double cl = Math.Cos(la), sl = Math.Sin(la), cd = Math.Cos(dl);
        double x = cl * Math.Sin(dl), y = c.CosP * sl - c.SinP * cl * cd;
        z = (float)(c.SinP * sl + c.CosP * cl * cd);
        sx = (float)(c.Cx + c.R * x); sy = (float)(c.Cy - c.R * y);
        return z > 0;
    }

    static void DrawPolyline(SKCanvas cv, in Cam c, float[] a, SKRect clip, SKPath path)
    {
        bool pen = false;
        for (int i = 0; i < a.Length; i += 2)
        {
            bool vis = Project(c, a[i], a[i + 1], out float x, out float y, out _);
            if (vis && x > clip.Left - 300 && x < clip.Right + 300 && y > clip.Top - 300 && y < clip.Bottom + 300)
            { if (pen) path.LineTo(x, y); else { path.MoveTo(x, y); pen = true; } }
            else pen = false;
        }
    }

    static void Layer(SKCanvas cv, in Cam c, List<float[]> lines, SKRect clip, SKPaint p)
    {
        var path = linePath; path.Reset();   // reused (render thread only)
        foreach (var a in lines) DrawPolyline(cv, c, a, clip, path);
        cv.DrawPath(path, p);
    }

    // reusable point buckets (render thread only)
    static readonly List<SKPoint>[] buckets = Enumerable.Range(0, 10).Select(_ => new List<SKPoint>(8192)).ToArray();

    // 2026-10-24 (choppy intro): paints, paths, shaders and constant data kept between frames (render thread only)
    static readonly SKPaint fill = new(), dotPaint = new(), textPaint = new();
    static readonly SKPath linePath = new();
    static readonly ShaderSlot bgSlot = new(), atmoSlot = new(), oceanSlot = new(), vigSlot = new();
    static readonly SKPathEffect borderDash = SKPathEffect.CreateDash(new[] { 5f, 4f }, 0);
    static readonly float[] DashOrbit = { 18f, 10f };
    static readonly float[] Mid = { 0.135f, 0.35f, 0.55f, 0.735f, 0.915f };
    static readonly (int X, int Y)[] Corners = { (-1, -1), (1, -1), (1, 1), (-1, 1) };
    static readonly float[] VigStops = { 0f, 0.6f, 1f }, OceanStops = { 0f, 0.45f, 1f }, AtmoStops = { 0f, 0.98f / 1.28f, 1f / 1.28f, 1f };
    // the lat/lon grid's lines never change (were rebuilt as ~25 new arrays every frame)
    static readonly List<float[]> GridLines = MakeGrid();
    static List<float[]> MakeGrid()
    {
        var grid = new List<float[]>();
        for (int la = -75; la <= 75; la += 15) { var a = new float[362]; for (int i = 0; i <= 180; i++) { a[2 * i] = la; a[2 * i + 1] = -180 + i * 2; } grid.Add(a); }
        for (int lo = -180; lo < 180; lo += 15) { var a = new float[240]; for (int i = 0; i < 120; i++) { a[2 * i] = -89 + i * 1.5f; a[2 * i + 1] = lo; } grid.Add(a); }
        return grid;
    }
    // the dive's speed lines: the same Random((int)(t * 30)) sequences as before, drawn once per seed (Prepare)
    static readonly double[]?[] speedSeeds = new double[400][];
    static double[] SpeedLines(int seed)
    {
        if (seed >= 0 && seed < speedSeeds.Length && speedSeeds[seed] is { } hit) return hit;
        var rnd = new Random(seed); var v = new double[60 * 5];
        for (int i = 0; i < 60; i++)
        {
            v[i * 5] = rnd.NextDouble(); v[i * 5 + 1] = rnd.NextDouble(); v[i * 5 + 2] = rnd.NextDouble();   // angle, radius, length
            v[i * 5 + 3] = rnd.NextDouble(); v[i * 5 + 4] = rnd.NextDouble();                                // width, alpha
        }
        if (seed >= 0 && seed < speedSeeds.Length) speedSeeds[seed] = v;
        return v;
    }
    static void PrepareSpeedLines() { for (int i = 0; i < speedSeeds.Length; i++) SpeedLines(i); }

    // sk_canvas_draw_points with a point COUNT, so a reused buffer can be drawn without copying it into a new
    // array every frame (SkiaSharp's DrawPoints only takes a whole array). Falls back to ToArray if unavailable.
    [System.Runtime.InteropServices.DllImport("libSkiaSharp", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    static extern void sk_canvas_draw_points(IntPtr canvas, SKPointMode mode, IntPtr count, ref SKPoint points, IntPtr paint);
    static bool nativePoints = true;
    static void DrawPoints(SKCanvas cv, List<SKPoint> pts, SKPaint p)
    {
        if (nativePoints)
        {
            try
            {
                var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pts);
                sk_canvas_draw_points(cv.Handle, SKPointMode.Points, (IntPtr)span.Length, ref System.Runtime.InteropServices.MemoryMarshal.GetReference(span), p.Handle);
                GC.KeepAlive(cv); GC.KeepAlive(p);
                return;
            }
            catch (Exception) { nativePoints = false; }
        }
        cv.DrawPoints(SKPointMode.Points, pts.ToArray(), p);
    }

    static string? placeCity, placeCountry, placeName; static double coordsLat = double.NaN, coordsLon = double.NaN; static string coordsText = "";

    /// <summary>Draws the flight at <paramref name="t"/> seconds into the panel <paramref name="r"/>
    /// (already clipped by the caller to the panel's shape).</summary>
    public static void Draw(SKCanvas cv, SKRect r, RaceSnapshot s, double t, float alpha) => DrawCore(cv, r, s, t, alpha, false, t);

    static void DrawCore(SKCanvas cv, SKRect r, RaceSnapshot s, double t, float alpha, bool opener, double tm, bool splash = false)
    {
        var sc = scene;
        if (sc == null) return;
        var c = Camera(sc, t, r, opener, tm, splash);
        double R = c.R, d = c.D, R0 = c.R0, Rn = R * 360 / R0;   // Rn: zoom in the units the look was tuned in
        float fade = (splash ? Ease(tm / 0.18) : opener ? Ease(tm / 1.4) : Ease(t / 0.8)) * alpha;
        int save = cv.Save();
        cv.ClipRect(r);
        // space
        fill.Reset(); fill.Shader = bgSlot.Radial(new SKPoint(r.MidX, r.MidY + 40), r.Height * 1.2f, [new SKColor(10, 18, 34), new SKColor(2, 4, 10)], []);
        cv.DrawRect(r, fill);
        {
            var sp = fill; sp.Reset(); sp.IsAntialias = true;
            float sa = (float)(1 - d) * fade;
            foreach (var st in Stars)
            {
                sp.Color = A(SKColors.White, st.B * 0.8 * sa * (0.7 + 0.3 * Math.Sin(t * 3 + st.X * 40)));
                cv.DrawCircle(r.Left + st.X * r.Width - (float)t * 6 * st.R, r.Top + st.Y * r.Height, st.R, sp);
            }
        }
        // atmosphere + ocean
        if (Rn < 30000)
        {
            float Rf = (float)R;
            // (these follow the globe's size, so they are rebuilt whenever it zooms)
            fill.Reset(); fill.IsAntialias = true;
            fill.Shader = atmoSlot.Radial(new SKPoint(c.Cx, c.Cy), Rf * 1.28f, [A(Cyan, 0), A(Cyan, 0), A(Cyan, 0.30 * fade), A(Cyan, 0)], AtmoStops);
            cv.DrawCircle(c.Cx, c.Cy, Rf * 1.28f, fill);
            fill.Shader = oceanSlot.Radial(new SKPoint(c.Cx - Rf * 0.35f, c.Cy - Rf * 0.3f), Rf * 1.5f,
                [A(new SKColor(0x12, 0x2C, 0x52), fade), A(Navy0, fade), A(Navy1, fade)], OceanStops);
            cv.DrawCircle(c.Cx, c.Cy, Rf, fill);
        }
        else { fill.Reset(); fill.Color = A(Navy0, alpha); cv.DrawRect(r, fill); }
        // lat / lon grid
        float ga = 0.16f * fade * (1 - Ease(d * 3));
        if (ga > 0.01f)
        {
            fill.Reset(); fill.IsAntialias = true; fill.Style = SKPaintStyle.Stroke; fill.StrokeWidth = 0.8f; fill.Color = A(Cyan, ga);
            Layer(cv, c, GridLines, r, fill);
        }
        // coastlines (neon)
        float ca = fade * (1 - SStep(0.45, 0.7, d));
        if (ca > 0.01f)
        {
            var path = linePath; path.Reset();
            foreach (var a in sc.Coast) DrawPolyline(cv, c, a, r, path);
            fill.Reset(); fill.IsAntialias = true; fill.Style = SKPaintStyle.Stroke; fill.StrokeWidth = 5; fill.Color = A(Cyan, 0.10 * ca); fill.StrokeJoin = SKStrokeJoin.Round;
            cv.DrawPath(path, fill);
            fill.StrokeWidth = 1.3f; fill.Color = A(Cyan, 0.75 * ca);
            cv.DrawPath(path, fill);
        }
        // land dots, four levels of detail cross-fading as we zoom
        var L = (X: -0.55, Y: 0.45, Z: 0.70); double ln = Math.Sqrt(L.X * L.X + L.Y * L.Y + L.Z * L.Z); L = (L.X / ln, L.Y / ln, L.Z / ln);
        double heatOn = SStep(1.5, 3.2, t), sweepOn = SStep(4.0, 5.0, t);
        for (int k = 0; k < sc.Levels.Length; k++)
        {
            var lv = sc.Levels[k];
            double spx = lv.Step * R;
            double w = (1 - SStep(18, 90, spx) * (k == 3 ? 0.55 : 1)) * (k != 0 ? SStep(3.0, 6.5, spx) : 1) * fade;
            if (k == 3) w *= 1 - 0.45 * SStep(6.0, 6.6, t);
            if (w < 0.01) continue;
            foreach (var b in buckets) b.Clear();
            double l0 = c.Lon * Math.PI / 180;
            for (int i = 0; i < lv.N; i++)
            {
                double dl = lv.LonR[i] - l0;
                double cd = Math.Cos(dl), cl = lv.CosLat[i], sl = lv.SinLat[i];
                double z = c.SinP * sl + c.CosP * cl * cd;
                if (z <= 0) continue;
                double x = cl * Math.Sin(dl), y = c.CosP * sl - c.SinP * cl * cd;
                float px = (float)(c.Cx + R * x), py = (float)(c.Cy - R * y);
                if (px < r.Left - 20 || px > r.Right + 20 || py < r.Top - 20 || py > r.Bottom + 20) continue;
                double lit = 0.30 + 0.70 * Math.Clamp(x * L.X + y * L.Y + z * L.Z, 0, 1);
                if (Rn < 30000) lit *= Math.Sqrt(z);
                lit *= lv.Shade[i];
                double sw = 0.5 + 0.5 * Math.Sin(Math.Sqrt((px - c.Cx) * (px - c.Cx) + (py - c.Cy) * (py - c.Cy)) * 0.02 - t * 6);
                lit = Math.Clamp(lit * (0.85 + 0.3 * sw * sweepOn), 0, 1);
                if (lit < 0.02) continue;
                double dLat = lv.Lat[i] - sc.Lat, dLon = (lv.Lon[i] - sc.Lon) * 0.67;
                double heat = Math.Exp(-Math.Sqrt(dLat * dLat + dLon * dLon) / lv.HeatScale) * heatOn;
                int bucket = lit < 0.25 ? 0 : lit < 0.45 ? 1 : lit < 0.65 ? 2 : lit < 0.82 ? 3 : 4;
                buckets[bucket + (heat >= 0.35 ? 5 : 0)].Add(new SKPoint(px, py));
            }
            float size = (float)Math.Clamp(spx * 0.34, 1.5, 8.0);
            var mid = Mid;
            var dp = dotPaint; dp.Reset(); dp.IsAntialias = true; dp.StrokeWidth = size; dp.StrokeCap = SKStrokeCap.Round;
            for (int bi = 0; bi < 10; bi++)
            {
                if (buckets[bi].Count == 0) continue;
                dp.Color = A(bi < 5 ? Cyan : Warm, w * (0.25 + 0.75 * mid[bi % 5]));
                DrawPoints(cv, buckets[bi], dp);   // straight from the reused bucket (was a new array per bucket per frame)
            }
        }
        // borders, rivers, motorways, city lights, town names
        float ba = fade * (1 - SStep(0.55, 0.8, d));
        if (ba > 0.01f)
        {
            fill.Reset(); fill.IsAntialias = true; fill.Style = SKPaintStyle.Stroke; fill.StrokeWidth = 0.9f; fill.Color = A(Cyan, 0.45 * ba); fill.PathEffect = borderDash;
            Layer(cv, c, sc.Borders, r, fill);
        }
        float ra = SStep(1500, 6000, Rn) * (1 - SStep(0.85, 0.97, d)) * alpha;
        if (ra > 0.01f)
        {
            fill.Reset(); fill.IsAntialias = true; fill.Style = SKPaintStyle.Stroke; fill.StrokeWidth = 1.4f; fill.Color = A(River, 0.55 * ra); fill.StrokeJoin = SKStrokeJoin.Round;
            Layer(cv, c, sc.Rivers, r, fill);
        }
        float wa = SStep(12000, 40000, Rn) * (1 - 0.6f * SStep(6.0, 6.6, t)) * alpha;
        if (wa > 0.01f)
        {
            var path = linePath; path.Reset();
            foreach (var a in sc.Roads) DrawPolyline(cv, c, a, r, path);
            fill.Reset(); fill.IsAntialias = true; fill.Style = SKPaintStyle.Stroke; fill.StrokeWidth = 5; fill.Color = A(Amber, 0.10 * wa); fill.StrokeJoin = SKStrokeJoin.Round;
            cv.DrawPath(path, fill);
            fill.StrokeWidth = 1.6f; fill.Color = A(Amber, 0.65 * wa);
            cv.DrawPath(path, fill);
        }
        float la2 = fade * (1 - 0.5f * SStep(0.7, 0.95, d));
        {
            var gp = dotPaint; gp.Reset(); gp.FilterQuality = SKFilterQuality.Medium; gp.Color = A(SKColors.White, 0.42 * la2);
            var core = fill; core.Reset(); core.IsAntialias = true; core.Color = A(new SKColor(0xFF, 0xF4, 0xE0), 0.95 * la2);
            var g = Glow();
            double zoomBoost = 1 + 0.25 * Clamp01(Math.Log10(Math.Max(1, Rn / 360)));
            foreach (var city in sc.Cities)
            {
                if (!Project(c, city.Lat, city.Lon, out float x, out float y, out _)) continue;
                if (x < r.Left - 40 || x > r.Right + 40 || y < r.Top - 40 || y > r.Bottom + 40) continue;
                float rr = (float)(Math.Clamp(Math.Log10(Math.Max(city.Pop, 1e4)) - 3.6, 0.6, 3.6) * zoomBoost);
                cv.DrawImage(g, new SKRect(x - rr * 3, y - rr * 3, x + rr * 3, y + rr * 3), gp);
                cv.DrawCircle(x, y, rr * 0.7f, core);
            }
        }
        float na = SStep(25000, 70000, Rn) * (1 - SStep(5.6, 6.0, t)) * alpha;
        if (na > 0.01f)
        {
            var tp = textPaint; tp.Reset(); tp.IsAntialias = true; tp.Typeface = Fonts.BarlowCondensed.SemiBold; tp.TextSize = 19; tp.Color = A(new SKColor(0xDC, 0xE6, 0xF2), 0.85 * na);
            foreach (var city in sc.Labels)
                if (Project(c, city.Lat, city.Lon, out float x, out float y, out _) && x > r.Left && x < r.Right - 120 && y > r.Top + 20 && y < r.Bottom - 10)
                    TextBlobs.Draw(cv, city.Upper, x + 12, y + 6, tp);
        }
        // orbit ring + HUD ticks (before the dive)
        float oa = fade * (1 - SStep(0.0, 0.25, d));
        if (oa > 0.01f)
        {
            float Rf = (float)R;
            cv.Save(); cv.Translate(c.Cx, c.Cy); cv.RotateDegrees(-16);
            // (the dash phase moves every frame, so this one path effect is still made per frame - the ring is only up briefly)
            using (var dash = SKPathEffect.CreateDash(DashOrbit, (float)(-t * 60)))
            {
                fill.Reset(); fill.IsAntialias = true; fill.Style = SKPaintStyle.Stroke; fill.StrokeWidth = 1.6f; fill.Color = A(Cyan, 0.55 * oa); fill.PathEffect = dash;
                cv.DrawOval(new SKRect(-Rf * 1.42f, -Rf * 0.30f, Rf * 1.42f, Rf * 0.30f), fill);
                fill.PathEffect = null;
            }
            double ang = t * 0.55;
            fill.Reset(); fill.IsAntialias = true; fill.Color = A(SKColors.White, oa);
            cv.DrawCircle((float)(Rf * 1.42 * Math.Cos(ang)), (float)(Rf * 0.30 * Math.Sin(ang)), 5, fill);
            cv.Restore();
            var tp = fill; tp.Reset(); tp.IsAntialias = true; tp.StrokeWidth = 1.4f; tp.Color = A(Cyan, 0.45 * oa);
            for (int i = 0; i < 120; i++)
            {
                double an = (i * 3 + t * 8) * Math.PI / 180; float len = i % 10 == 0 ? 14 : 6, r0 = Rf * 1.13f;
                cv.DrawLine(c.Cx + r0 * (float)Math.Cos(an), c.Cy + r0 * (float)Math.Sin(an), c.Cx + (r0 + len) * (float)Math.Cos(an), c.Cy + (r0 + len) * (float)Math.Sin(an), tp);
            }
        }
        // target brackets locking on
        string place = PlaceName(s);
        if (Project(c, sc.Lat, sc.Lon, out float tx, out float ty, out _))
        {
            float lk = SStep(1.6, 2.2, t) * (1 - SStep(4.6, 5.2, t)) * alpha;
            if (lk > 0.01f)
            {
                float sz = 26 + 140 * (1 - Ease((t - 1.6) / 1.4));
                var bp = fill; bp.Reset(); bp.IsAntialias = true; bp.Style = SKPaintStyle.Stroke; bp.StrokeWidth = 3; bp.Color = A(SKColors.White, lk);
                foreach (var (sx, sy) in Corners)
                {
                    cv.DrawLine(tx + sx * sz, ty + sy * sz, tx + sx * (sz - 14), ty + sy * sz, bp);
                    cv.DrawLine(tx + sx * sz, ty + sy * sz, tx + sx * sz, ty + sy * (sz - 14), bp);
                }
                var fp = textPaint; fp.Reset(); fp.IsAntialias = true; fp.Typeface = Fonts.BarlowCondensed.Bold; fp.TextSize = 20; fp.Color = A(SKColors.White, lk);
                TextBlobs.Draw(cv, CoordsMemo(sc.Lat, sc.Lon), tx + sz + 14, ty - sz + 16, fp);
                fp.Color = A(Cyan, lk);
                TextBlobs.Draw(cv, t < 3.0 ? "LOCKING ON" : place, tx + sz + 14, ty - sz + 40, fp);
            }
        }
        // speed lines during the dive
        float va = (float)Math.Sin(Clamp01(d) * Math.PI) * 0.9f * alpha;
        if (va > 0.02f)
        {
            var rv = SpeedLines((int)(t * 30));   // the same Random((int)(t * 30)) values, drawn once per seed
            var lp = fill; lp.Reset(); lp.IsAntialias = true; lp.StrokeCap = SKStrokeCap.Round;
            for (int i = 0; i < 60; i++)
            {
                double an = rv[i * 5] * Math.PI * 2, r0 = 200 + rv[i * 5 + 1] * 520, len = (60 + rv[i * 5 + 2] * 220) * va;
                lp.StrokeWidth = 1 + 2 * (float)rv[i * 5 + 3]; lp.Color = A(i % 3 != 0 ? Cyan : SKColors.White, va * (0.2 + 0.5 * rv[i * 5 + 4]));
                cv.DrawLine(c.Cx + (float)(r0 * Math.Cos(an)), c.Cy + (float)(r0 * Math.Sin(an)), c.Cx + (float)((r0 + len) * Math.Cos(an)), c.Cy + (float)((r0 + len) * Math.Sin(an)), lp);
            }
        }
        if (opener) goto Vignette;
        // the circuit draws itself
        float ka = SStep(5.7, 6.2, t) * alpha;
        if (ka > 0.01f && sc.Track is { } tr)
        {
            float minX = tr.Min(p => p.X), maxX = tr.Max(p => p.X), minY = tr.Min(p => p.Y), maxY = tr.Max(p => p.Y);
            float scl = Math.Min(r.Width * 0.8f / (maxX - minX), r.Height * 0.5f / (maxY - minY));
            float mx = (minX + maxX) / 2, my = (minY + maxY) / 2;
            int n = Math.Max(2, (int)(tr.Length * Ease((t - 5.8) / 1.4)));
            using var path = new SKPath();
            path.MoveTo(c.Cx + (tr[0].X - mx) * scl, c.Cy + (tr[0].Y - my) * scl);
            for (int i = 1; i < n; i++) path.LineTo(c.Cx + (tr[i].X - mx) * scl, c.Cy + (tr[i].Y - my) * scl);
            foreach (var (wdt, aa, col) in new[] { (26f, 0.10, Cyan), (12f, 0.25, Cyan), (5f, 1.0, SKColors.White) })
                using (var tp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = wdt, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round, Color = A(col, aa * ka) })
                    cv.DrawPath(path, tp);
            var head = tr[n - 1];
            using (var hp = new SKPaint { IsAntialias = true, Color = A(SKColors.White, ka) }) cv.DrawCircle(c.Cx + (head.X - mx) * scl, c.Cy + (head.Y - my) * scl, 9, hp);
        }
        // pin drop
        double pk = (t - 5.5) / 0.55;
        if (pk > 0)
        {
            float drop = OutBack(pk), pa = Clamp01(pk * 3) * alpha;
            float px = c.Cx, py = c.Cy - (1 - drop) * 220;
            if (t > 6.0)
                for (int i = 0; i < 3; i++)
                {
                    double rk = ((t - 6.0) - i * 0.45) % 1.35;
                    using var rp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = A(Red, (1 - rk / 1.35) * 0.9 * alpha) };
                    cv.DrawCircle(c.Cx, c.Cy, (float)(20 + rk * 120), rp);
                }
            using (var pin = new SKPath())
            {
                pin.MoveTo(px, py); pin.CubicTo(px - 10, py - 22, px - 30, py - 38, px - 30, py - 62);
                pin.ArcTo(new SKRect(px - 30, py - 92, px + 30, py - 32), 180, 180, false);
                pin.CubicTo(px + 30, py - 38, px + 10, py - 22, px, py); pin.Close();
                using var pp = new SKPaint { IsAntialias = true, Color = A(Red, pa) };
                cv.DrawPath(pin, pp);
            }
            using (var wp = new SKPaint { IsAntialias = true, Color = A(SKColors.White, pa) }) cv.DrawCircle(px, py - 62, 12, wp);
            using (var shp = new SKPaint { IsAntialias = true, Color = A(SKColors.Black, 0.5 * pa) }) cv.DrawOval(new SKRect(c.Cx - 14 * drop, c.Cy - 4, c.Cx + 14 * drop, c.Cy + 4), shp);
        }
        // readout
        float ro = SStep(6.2, 6.6, t) * alpha;
        if (ro > 0.01f)
        {
            float x0 = r.Left + 70, y0 = r.Bottom - 170;
            using (var bar = new SKPaint { Color = A(Red, ro) }) cv.DrawRect(new SKRect(x0 - 30, y0 - 70, x0 - 22, y0 + 130), bar);
            var flag = FlagFor(s);
            if (flag != null)
                using (var fp = new SKPaint { FilterQuality = SKFilterQuality.High, Color = A(SKColors.White, ro) })
                    cv.DrawBitmap(flag, new SKRect(x0, y0 - 66, x0 + 30f * flag.Width / flag.Height, y0 - 36), fp);
            string Typed(string str, double t0) => str[..(int)(str.Length * Clamp01((t - t0) / 0.5))];
            string name = (s.TrackDisplayName ?? "").ToUpperInvariant();
            using (var np = new SKPaint { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Black, TextSize = 64, Color = A(SKColors.White, ro) })
            {
                while (np.MeasureText(name) > r.Width - 140 && np.TextSize > 30) np.TextSize -= 2;
                cv.DrawText(Typed(name, 6.25), x0, y0 + 30, np);
            }
            using (var pp = new SKPaint { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 30, Color = A(Cyan, ro) })
                cv.DrawText(Typed(place, 6.45), x0, y0 + 72, pp);
            string len = string.IsNullOrWhiteSpace(s.Extras.TrackLength) ? "" : "    ·    " + s.Extras.TrackLength.ToUpperInvariant();
            double tod = s.SessionTimeOfDay;
            string local = tod > 0 ? string.Format(CultureInfo.InvariantCulture, "    ·    LOCAL TIME {0:00}:{1:00}", (int)(tod / 3600) % 24, (int)(tod / 60) % 60) : "";
            using (var ip = new SKPaint { IsAntialias = true, Typeface = Fonts.BarlowCondensed.SemiBold, TextSize = 26, Color = A(new SKColor(0xB8, 0xC0, 0xCE), ro) })
                cv.DrawText(Typed(Coords(sc.Lat, sc.Lon, true) + len + local, 6.65), x0, y0 + 112, ip);
        }
        Vignette:
        // vignette
        fill.Reset();
        fill.Shader = vigSlot.Radial(new SKPoint(r.MidX, r.MidY), Math.Max(r.Width, r.Height) * 0.75f, [A(SKColors.Black, 0), A(SKColors.Black, 0), A(SKColors.Black, 0.5 * alpha)], VigStops);
        cv.DrawRect(r, fill);
        cv.RestoreToCount(save);
    }

    static string PlaceName(RaceSnapshot s)
    {
        // made only when the names change (asked every frame)
        if (placeName != null && s.Extras.TrackCity == placeCity && s.Extras.TrackCountry == placeCountry) return placeName;
        placeCity = s.Extras.TrackCity; placeCountry = s.Extras.TrackCountry;
        return placeName = PlaceNameOf(s);
    }

    static string CoordsMemo(double lat, double lon)
    {
        if (lat != coordsLat || lon != coordsLon) { coordsText = Coords(lat, lon, false); coordsLat = lat; coordsLon = lon; }
        return coordsText;
    }

    static string PlaceNameOf(RaceSnapshot s)
    {
        var city = (s.Extras.TrackCity ?? "").Trim(); var country = (s.Extras.TrackCountry ?? "").Trim();
        if (city.Length == 0) return country.ToUpperInvariant();
        return (country.Length > 0 ? city + "  ·  " + country : city).ToUpperInvariant();
    }

    static string Coords(double lat, double lon, bool wide) => string.Format(CultureInfo.InvariantCulture,
        wide ? "{0:0.0000}° {1}    {2:0.0000}° {3}" : "{0:0.0000}°{1}  {2:0.0000}°{3}", Math.Abs(lat), lat >= 0 ? "N" : "S", Math.Abs(lon), lon >= 0 ? "E" : "W");

    static SKBitmap? flagBmp; static string? flagKey;
    static SKBitmap? FlagFor(RaceSnapshot s)
    {
        var code = s.TrackCountryCode?.ToLowerInvariant();
        if (code == flagKey) return flagBmp;
        flagKey = code; flagBmp = null;
        try { var p = code == null ? null : Assets.Resolve("flags", code + ".png"); if (p != null && File.Exists(p)) flagBmp = Assets.Decode(p); } catch { }
        return flagBmp;
    }
}
