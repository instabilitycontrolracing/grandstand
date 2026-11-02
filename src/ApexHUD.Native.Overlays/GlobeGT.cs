using System.IO;
using System.Globalization;
using System.Text.Json;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// The GT7-style globe for the opener's globe beat (2026-10-21, Angelo: "this looks perfect, I want
/// this wired in"). A matte, lit Earth - teal sea, ink-navy land with shaded mountains, thin borders,
/// a cyan atmosphere on the horizon - flies in from high over Europe/Asia, tilts toward the horizon and
/// settles on the circuit, where a glowing red pin lands. Country and region names are set in spaced
/// Cormorant Garamond capitals.
///
/// <para>How it stays cheap: the planet's pictures (assets\globe: one world texture plus a sharper
/// texture for the region the circuit is in) are decoded once in the background. Each frame the visible
/// cap of the sphere is a few thousand textured triangles: the graphics card stretches the picture over
/// them and the lighting rides on the corners. No per-pixel work on the processor.</para>
///
/// <para>Falls back to the older dot globe (GlobeIntro) when the textures aren't installed.</para>
/// </summary>
public static class GlobeGT
{
#pragma warning disable CS0649   // filled from tiles.json
    sealed class Tile { public string Name = "", File = ""; public double Lon0, Lon1, Lat0, Lat1, Ppd; }
#pragma warning restore CS0649
    sealed record Label(string Name, double Lat, double Lon, double Rank)
    {
        public string Upper { get; } = Name.ToUpperInvariant();   // drawn in capitals; worked out once, not per frame
    }

    static SKBitmap? worldBmp, detailBmp;
    static SKImage? worldImg, detailImg;
    static Tile? worldTile, detailTile;
    static List<Label>? countries;
    static List<(string Name, double Lat, double Lon, string Adm)>? allRegions;
    static List<Label> regions = new();
    static volatile string? key;
    static string? building;
    static bool broken;
    static double tLat = double.NaN, tLon = double.NaN;

    static string Dir => Assets.Resolve("globe");
    // checked at most every few seconds (it's asked every frame pre-race: two file look-ups + path strings each time)
    static bool installed, installedKnown; static long installedAt;
    public static bool Installed
    {
        get
        {
            long now = Environment.TickCount64;
            if (!installedKnown || now - installedAt > 5000)
            {
                installedKnown = true;
                installed = File.Exists(Path.Combine(Dir, "tiles.json")) && File.Exists(Path.Combine(Dir, "global.jpg"));
                installedAt = now;
            }
            return installed;
        }
    }

    static string KeyOf(double lat, double lon) => string.Format(CultureInfo.InvariantCulture, "{0:0.0000}_{1:0.0000}", lat, lon);
    // Prepare / Ready run every frame: the key string is made only when the coordinates change (2026-10-24)
    static double memoLat = double.NaN, memoLon = double.NaN; static string memoKey = "";
    static string KeyMemo(double lat, double lon)
    {
        if (lat != memoLat || lon != memoLon) { memoKey = KeyOf(lat, lon); memoLat = lat; memoLon = lon; }
        return memoKey;
    }

    /// <summary>Cheap; call every frame pre-race. Loads the pictures for this circuit in the background.</summary>
    public static void Prepare(double lat, double lon)
    {
        if (broken || double.IsNaN(lat) || double.IsNaN(lon) || !Installed) return;
        string k = KeyMemo(lat, lon);
        if (key == k || building == k) return;
        building = k;
        StartLoad(lat, lon, k);
    }

    // a separate method so the background job's closure is only allocated when a load starts (a lambda in
    // Prepare itself captured its parameters, which allocates on every call - i.e. every frame)
    static void StartLoad(double lat, double lon, string k)
    {
        Task.Run(() =>
        {
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, IncludeFields = true };
                var tiles = JsonSerializer.Deserialize<List<Tile>>(File.ReadAllText(Path.Combine(Dir, "tiles.json")), opts) ?? new();
                if (worldImg == null)
                {
                    worldTile = tiles.First(t => t.Name == "global");
                    (worldBmp, worldImg) = Load(worldTile.File);
                }
                var dt = tiles.FirstOrDefault(t => t.Name != "global" && lat > t.Lat0 + 3 && lat < t.Lat1 - 3 && lon > t.Lon0 + 4 && lon < t.Lon1 - 4);
                if (dt == null) { detailTile = null; detailImg = null; }
                else if (detailTile?.Name != dt.Name)
                {
                    var (b, i) = Load(dt.File);
                    detailBmp = b; detailImg = i; detailTile = dt;
                }
                LoadLabels();
                grainBmp ??= BuildGrain();   // the film grain picture, made here instead of on the beat's first frame
                double cl = Math.Cos(lat * Math.PI / 180);
                // only the circuit's own country's regions (no neighbouring-country clutter)
                var home = allRegions!.OrderBy(g => Sq(g.Lat - lat) + Sq((g.Lon - lon) * cl)).FirstOrDefault().Adm;
                regions = allRegions!.Where(g => g.Adm == home && Math.Abs(g.Lat - lat) < 6 && Math.Abs(g.Lon - lon) * cl < 6)
                                     .OrderBy(g => Sq(g.Lat - lat) + Sq((g.Lon - lon) * cl)).Take(24)
                                     .Select(g => new Label(g.Name, g.Lat, g.Lon, 0)).ToList();
                tLat = lat; tLon = lon; key = k;
            }
            catch (Exception) { broken = true; }
            finally { if (building == k) building = null; }
        });
    }

    public static bool Ready(RaceSnapshot s) => key != null && worldImg != null && key == KeyMemo(s.TrackLatitude, s.TrackLongitude);

    static (SKBitmap, SKImage) Load(string file)
    {
        using var data = SKData.Create(Path.Combine(Dir, file));
        var bmp = SKBitmap.Decode(data) ?? throw new IOException("decode " + file);
        bmp.SetImmutable();
        return (bmp, SKImage.FromBitmap(bmp));
    }

    // 2026-10-24 (choppy intro): the world (134 MB on the card) + regional (~100 MB) pictures overflowed Skia's
    // picture memory, so they were thrown out and re-sent to the graphics card every frame (50-125 ms a frame).
    // Pinned on the card once - render thread - and never re-sent.
    static SKImage? pinnedWorldSrc, pinnedDetailSrc;
    static void PinPictures()
    {
        var w = worldImg;
        if (w != null && !w.IsTextureBacked && !ReferenceEquals(w, pinnedWorldSrc)) { pinnedWorldSrc = w; worldImg = GpuContext.Pin(w); }
        var d = detailImg;
        if (d != null && !d.IsTextureBacked && !ReferenceEquals(d, pinnedDetailSrc)) { pinnedDetailSrc = d; detailImg = GpuContext.Pin(d); }
    }

    /// <summary>#519 (2026-10-29, Angelo: free up graphics memory): once the race is running the globe isn't needed until the
    /// next intro, so its two pinned pictures (~235 MB on the RTX 3080 Ti) go back to plain memory copies; the intro's
    /// warm-up (<see cref="Warm"/>) pins them again before the next globe beat. Render thread only. True if it freed any.</summary>
    public static bool ReleaseGpu()
    {
        bool freed = false;
        if (worldImg != null && worldImg.IsTextureBacked && pinnedWorldSrc != null)
        { var t = worldImg; worldImg = pinnedWorldSrc; pinnedWorldSrc = null; t.Dispose(); freed = true; }
        if (detailImg != null && detailImg.IsTextureBacked && pinnedDetailSrc != null)
        { var t = detailImg; detailImg = pinnedDetailSrc; pinnedDetailSrc = null; t.Dispose(); freed = true; }
        return freed;
    }

    static void LoadLabels()
    {
        if (countries != null) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "labels.json")));
        var c = new List<Label>();
        foreach (var e in doc.RootElement.GetProperty("countries").EnumerateArray())
            c.Add(new Label(e[0].GetString() ?? "", e[1].GetDouble(), e[2].GetDouble(), e[3].GetDouble()));
        var r = new List<(string, double, double, string)>();
        foreach (var e in doc.RootElement.GetProperty("regions").EnumerateArray())
            r.Add((e[0].GetString() ?? "", e[1].GetDouble(), e[2].GetDouble(), e[3].GetString() ?? ""));
        allRegions = r;
        countries = c.OrderBy(x => x.Rank).ToList();
    }

    // ------------------------------------------------------------------ small vector maths
    readonly record struct V(double X, double Y, double Z)
    {
        public static V operator +(V a, V b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V operator -(V a, V b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V operator *(V a, double k) => new(a.X * k, a.Y * k, a.Z * k);
        public double Dot(V b) => X * b.X + Y * b.Y + Z * b.Z;
        public V Cross(V b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Len => Math.Sqrt(X * X + Y * Y + Z * Z);
        public V N => this * (1 / Len);
    }
    static V Geo(double lat, double lon)
    {
        double la = lat * Math.PI / 180, lo = lon * Math.PI / 180;
        return new V(Math.Cos(la) * Math.Cos(lo), Math.Cos(la) * Math.Sin(lo), Math.Sin(la));
    }
    static double Sq(double x) => x * x;
    static float Clamp01(double x) => (float)Math.Clamp(x, 0, 1);
    static float Ease(double x) { float v = Clamp01(x); return v * v * (3 - 2 * v); }
    static float SS(double e0, double e1, double x) => Ease((x - e0) / (e1 - e0));
    static double EaseOut3(double x) { double v = Math.Clamp(x, 0, 1); return 1 - Math.Pow(1 - v, 3); }

    sealed class Cam
    {
        public V C, F, R, U, L;
        public double K, Aspect, ShiftY, Alt;
        public SKRect Rect;
        public bool Project(V p, out float x, out float y, out double z)
        {
            var q = p - C; z = q.Dot(F);
            if (z <= 1e-5) { x = y = 0; return false; }
            x = Rect.Left + (float)((q.Dot(R) / z / K / Aspect + 1) / 2 * Rect.Width);
            y = Rect.Top + (float)((1 - q.Dot(U) / z / K - ShiftY) / 2 * Rect.Height);
            return true;
        }
    }

    /// <summary>The flight: high over Europe/Asia (for Le Mans) drifting west onto the circuit, coming down
    /// and tilting toward the horizon, settling over the track as the bar ends.</summary>
    static readonly Cam camBuf = new();   // one camera, refilled every frame (render thread only)

    static Cam MakeCam(double u, double dur, SKRect r, out float pin, Cam? into = null)
    {
        double k = EaseOut3(u / dur);
        double sLat = tLat >= 0 ? tLat - 8 : tLat + 8;
        double lat = sLat + (tLat - sLat) * k, lon = tLon + 42 * (1 - k);
        double alt = Math.Exp(Math.Log(1.55) * (1 - k) + Math.Log(0.17) * k);
        double tilt = (12 + 44 * k) * Math.PI / 180, roll = (-14 + 2 * k) * Math.PI / 180;
        pin = Ease((u - dur * 0.78) / (dur * 0.22));
        var T = Geo(lat, lon);
        var east = new V(0, 0, 1).Cross(T).N; var north = T.Cross(east);
        var C = T + (T * Math.Cos(tilt) - north * Math.Sin(tilt)) * alt;
        var f = (T - C).N; var rr = f.Cross(north).N; var uu = rr.Cross(f);
        var R = rr * Math.Cos(roll) + uu * Math.Sin(roll);
        var U = uu * Math.Cos(roll) - rr * Math.Sin(roll);
        var cam = into ?? camBuf;
        cam.C = C; cam.F = f; cam.R = R; cam.U = U; cam.K = Math.Tan(34.0 * Math.PI / 360); cam.Aspect = r.Width / r.Height; cam.ShiftY = -0.18 * k; cam.Alt = alt; cam.Rect = r;
        cam.L = (R * -0.75 + U * 0.85 + f * -0.55).N;
        return cam;
    }

    // ------------------------------------------------------------------ mesh buffers (render thread only)
    static readonly int NR = Env("GLOBE_NR", 96), NA = Env("GLOBE_NA", 256);
    static readonly SKFilterQuality FQ = (SKFilterQuality)Env("GLOBE_FQ", 1);   // bilinear: the texture is never shrunk much, and mip-mapping is very slow per triangle on the CPU path
    static int Env(string n, int d) => int.TryParse(Environment.GetEnvironmentVariable(n), out int v) ? v : d;
    static readonly SKPoint[] gPos = new SKPoint[(NR + 1) * NA];
    static readonly bool[] gOk = new bool[(NR + 1) * NA];
    static readonly double[] gLat = new double[(NR + 1) * NA], gLon = new double[(NR + 1) * NA];
    static readonly SKColor[] gShade = new SKColor[(NR + 1) * NA], gRim = new SKColor[(NR + 1) * NA];
    // 2026-10-24 (choppy intro, round 2): the sphere point of every ring vertex is kept, and the expensive part
    // (latitude/longitude, lighting, rim) is only worked out for vertices of quads that are actually on screen.
    static readonly double[] gX = new double[(NR + 1) * NA], gY = new double[(NR + 1) * NA], gZ = new double[(NR + 1) * NA];
    static readonly int[] gNeed = new int[(NR + 1) * NA];
    static readonly int[] keptQuads = new int[NR * NA];
    static int stamp;
    static readonly double[] cosA = Ring(true), sinA = Ring(false);   // the ring angles never change: worked out once
    static double[] Ring(bool cos)
    {
        var v = new double[NA];
        for (int a = 0; a < NA; a++) { double phi = 2 * Math.PI * a / NA; v[a] = cos ? Math.Cos(phi) : Math.Sin(phi); }
        return v;
    }
    // 2026-10-24 (Angelo: "the globe part is horribly choppy"): the vertex lists used to be copied into brand-new
    // arrays every frame (several MB, on the large-object heap -> full garbage collections mid-animation).
    // Now the triangles are written straight into arrays that are kept and reused; unused tail vertices are
    // zeroed (zero-area triangles), exactly as the pooled copies were.
    sealed class VBuf
    {
        public SKPoint[] Pos, Tex; public SKColor[] Col, Col2;
        public int N, Used;
        public VBuf(int cap) { Pos = new SKPoint[cap]; Tex = new SKPoint[cap]; Col = new SKColor[cap]; Col2 = new SKColor[cap]; }
        public void Begin(int need)
        {
            if (Pos.Length < need)
            {
                int len = Math.Max(need, Pos.Length * 2) + 3;
                Pos = new SKPoint[len]; Tex = new SKPoint[len]; Col = new SKColor[len]; Col2 = new SKColor[len]; Used = 0;
            }
            N = 0;
        }
        /// <summary>Zero what the previous frame used beyond this frame's count.</summary>
        public void End(bool tex, bool col2)
        {
            if (Used > N)
            {
                Array.Clear(Pos, N, Used - N); Array.Clear(Col, N, Used - N);
                if (tex) Array.Clear(Tex, N, Used - N);
                if (col2) Array.Clear(Col2, N, Used - N);
            }
            Used = N;
        }
    }
    static VBuf world = new(3), detail = new(3);
    static readonly VBuf halo = new(4 * 256 * 6 + 3);
    // 2026-10-24: the triangle buffers are sized for the whole flight in the background (Warm) before the beat,
    // so they never grow - a big new array mid-animation is a large-object allocation (and a full collection).
    static volatile VBuf[]? sizedBufs; static float sizedFor = -1; static bool sizing;

    /// <summary>The most on-screen quads any frame of the flight will have for this screen rect.</summary>
    static int MaxQuads(SKRect r)
    {
        var cam = new Cam(); var pos = new SKPoint[(NR + 1) * NA]; var ok = new bool[(NR + 1) * NA];
        int best = 0;
        double dur = GridIntro.GlobeBarS;
        for (int step = 0; step <= 72; step++)
        {
            var c = MakeCam(dur * step / 72, dur, r, out _, cam);
            double cl = c.C.Len; var cd = c.C * (1 / cl);
            double thMax = Math.Acos(1 / cl);
            var e1 = (Math.Abs(cd.Z) < 0.95 ? cd.Cross(new V(0, 0, 1)) : cd.Cross(new V(1, 0, 0))).N; var e2 = cd.Cross(e1);
            for (int i = 0; i <= NR; i++)
            {
                double f = (double)i / NR, th = thMax * (1 - (1 - f) * (1 - f));
                double ct = Math.Cos(th), st = Math.Sin(th);
                for (int a = 0; a < NA; a++)
                {
                    int id = i * NA + a;
                    ok[id] = c.Project(cd * ct + (e1 * cosA[a] + e2 * sinA[a]) * st, out float x, out float y, out _);
                    pos[id] = new SKPoint(x, y);
                }
            }
            float mx = r.Width * 0.15f, my = r.Height * 0.15f;
            int nq = 0;
            for (int i = 0; i < NR; i++)
                for (int a = 0; a < NA; a++)
                {
                    int a1 = (a + 1) % NA, v0 = i * NA + a, v1 = i * NA + a1, v2 = (i + 1) * NA + a1, v3 = (i + 1) * NA + a;
                    if (!ok[v0] || !ok[v1] || !ok[v2] || !ok[v3]) continue;
                    float x0 = Math.Min(Math.Min(pos[v0].X, pos[v1].X), Math.Min(pos[v2].X, pos[v3].X)), x1 = Math.Max(Math.Max(pos[v0].X, pos[v1].X), Math.Max(pos[v2].X, pos[v3].X));
                    float y0 = Math.Min(Math.Min(pos[v0].Y, pos[v1].Y), Math.Min(pos[v2].Y, pos[v3].Y)), y1 = Math.Max(Math.Max(pos[v0].Y, pos[v1].Y), Math.Max(pos[v2].Y, pos[v3].Y));
                    if (x1 < r.Left - mx || x0 > r.Right + mx || y1 < r.Top - my || y0 > r.Bottom + my) continue;
                    nq++;
                }
            best = Math.Max(best, nq);
        }
        return best;
    }
    static SKBitmap? grainBmp;

    // paints and shaders kept between frames (render thread only)
    static readonly SKPaint bgPaint = new(), worldPaint = new() { IsAntialias = true }, detailPaint = new() { IsAntialias = true },
        rimPaint = new() { BlendMode = SKBlendMode.Plus, IsAntialias = true }, lightPaint = new() { BlendMode = SKBlendMode.Plus },
        vigPaint = new(), grainPaint = new(), textPaint = new() { IsAntialias = true, SubpixelText = true }, pinPaint = new() { IsAntialias = true };
    static readonly SKShader white = SKShader.CreateColor(SKColors.White);
    static readonly ShaderSlot bgSlot = new(), worldSlot = new(), detailSlot = new(), vigSlot = new(), grainSlot = new(), pinGlowSlot = new(),
        warmWorld = new(), warmDetail = new();
    static readonly ShaderSlot[] lightSlots = { new(), new(), new(), new(), new(), new(), new(), new(), new() };
    static readonly List<SKRect> taken = new();
    // the nine drifting soft lights: the same Random(7) sequence the old per-frame code drew from
    static readonly (double Fx, double Sp, double Fy, double Fr, double Fi)[] lights = MakeLights();
    static (double, double, double, double, double)[] MakeLights()
    {
        var rnd = new Random(7); var l = new (double, double, double, double, double)[9];
        for (int i = 0; i < 9; i++)
        {
            double fx = rnd.NextDouble(), sp = 0.5 + rnd.NextDouble(), fy = rnd.NextDouble(), fr = 0.03 + 0.07 * rnd.NextDouble(), fi = 0.035 + 0.03 * rnd.NextDouble();
            l[i] = (fx, sp, fy, fr, fi);
        }
        return l;
    }

    /// <summary>Draws the globe beat. Returns false (draw nothing) if this circuit's globe isn't ready.</summary>
    public static bool DrawSplash(SKCanvas cv, SKRect r, RaceSnapshot s, double u, float alpha)
    {
        if (worldImg == null || worldTile == null || !Ready(s)) return false;
        PinPictures();
        long pt = Prof.T();
        double dur = GridIntro.GlobeBarS;
        var c = MakeCam(u, dur, r, out float pin);
        float fade = Ease(u / 0.18) * alpha;
        float W = r.Width, H = r.Height;
        int save = cv.Save();
        cv.ClipRect(r);

        // space
        bgPaint.Shader = bgSlot.Radial(new SKPoint(r.MidX, r.MidY), H * 0.9f, [new SKColor(8, 21, 29), new SKColor(3, 8, 14)], []);
        cv.DrawRect(r, bgPaint);

        pt = Prof.Lap(PSpace, pt);
        // ---- the visible cap of the sphere, as rings around the point under the camera
        double cl = c.C.Len; var cd = c.C * (1 / cl);
        double thMax = Math.Acos(1 / cl);
        var e1 = (Math.Abs(cd.Z) < 0.95 ? cd.Cross(new V(0, 0, 1)) : cd.Cross(new V(1, 0, 0))).N; var e2 = cd.Cross(e1);
        float mx = W * 0.15f, my = H * 0.15f;
        // 1) every ring vertex: its point on the sphere and where it lands on screen
        for (int i = 0; i <= NR; i++)
        {
            double f = (double)i / NR, th = thMax * (1 - (1 - f) * (1 - f));
            double ct = Math.Cos(th), st = Math.Sin(th);
            for (int a = 0; a < NA; a++)
            {
                int id = i * NA + a;
                var p = cd * ct + (e1 * cosA[a] + e2 * sinA[a]) * st;
                gOk[id] = c.Project(p, out float x, out float y, out _);
                gPos[id] = new SKPoint(x, y);
                gX[id] = p.X; gY[id] = p.Y; gZ[id] = p.Z;
            }
        }
        // 2) the quads that are on screen (same test as before), and the vertices they use
        int nq = 0; stamp++;
        for (int i = 0; i < NR; i++)
            for (int a = 0; a < NA; a++)
            {
                int a1 = (a + 1) % NA;
                int v0 = i * NA + a, v1 = i * NA + a1, v2 = (i + 1) * NA + a1, v3 = (i + 1) * NA + a;
                if (!gOk[v0] || !gOk[v1] || !gOk[v2] || !gOk[v3]) continue;
                float x0 = Math.Min(Math.Min(gPos[v0].X, gPos[v1].X), Math.Min(gPos[v2].X, gPos[v3].X)), x1 = Math.Max(Math.Max(gPos[v0].X, gPos[v1].X), Math.Max(gPos[v2].X, gPos[v3].X));
                float y0 = Math.Min(Math.Min(gPos[v0].Y, gPos[v1].Y), Math.Min(gPos[v2].Y, gPos[v3].Y)), y1 = Math.Max(Math.Max(gPos[v0].Y, gPos[v1].Y), Math.Max(gPos[v2].Y, gPos[v3].Y));
                if (x1 < r.Left - mx || x0 > r.Right + mx || y1 < r.Top - my || y0 > r.Bottom + my) continue;
                keptQuads[nq++] = v0;
                gNeed[v0] = stamp; gNeed[v1] = stamp; gNeed[v2] = stamp; gNeed[v3] = stamp;
            }
        // 3) latitude/longitude and lighting for just those vertices
        for (int id = 0; id < gNeed.Length; id++)
        {
            if (gNeed[id] != stamp) continue;
            var p = new V(gX[id], gY[id], gZ[id]);
            gLat[id] = Math.Asin(Math.Clamp(p.Z, -1, 1)) * 180 / Math.PI;
            gLon[id] = Math.Atan2(p.Y, p.X) * 180 / Math.PI;
            double lam = Math.Pow(Math.Clamp(p.Dot(c.L) * 0.5 + 0.5, 0, 1), 1.6);
            double shade = Math.Min(1, (0.30 + 1.05 * lam) / 1.35);
            byte sb = (byte)(255 * shade);
            gShade[id] = new SKColor(sb, sb, sb, 255);
            var dv = (p - c.C).N; double view = Math.Clamp(-p.Dot(dv), 0, 1), fres = Math.Pow(1 - view, 3);
            double rk = fres * (0.35 + 0.9 * lam) * 0.75, sh = Math.Pow(lam, 6);
            gRim[id] = new SKColor(B((0.35 * rk + 0.06 * sh) * fade), B((0.62 * rk + 0.16 * sh) * fade), B((0.68 * rk + 0.18 * sh) * fade), 255);
        }
        // 4) the triangles, in the same order as before
        if (sizedBufs is { } sized) { world = sized[0]; detail = sized[1]; sizedBufs = null; }   // buffers sized for the flight by Warm
        var wt = worldTile; var dtl = detailTile;
        float detailOn = dtl != null && detailImg != null ? SS(0.95, 0.55, c.Alt) : 0;
        byte fa = B(fade);
        world.Begin(nq * 6); detail.Begin(nq * 6);
        var wp = world.Pos; var wtx = world.Tex; var wc = world.Col; var rc = world.Col2;
        Span<int> q = stackalloc int[6];
        for (int k = 0; k < nq; k++)
        {
            int v0 = keptQuads[k], i = v0 / NA, a = v0 % NA, a1 = (a + 1) % NA;
            int v1 = i * NA + a1, v2 = (i + 1) * NA + a1, v3 = (i + 1) * NA + a;
            q[0] = v0; q[1] = v1; q[2] = v2; q[3] = v0; q[4] = v2; q[5] = v3;
            for (int t = 0; t < 6; t += 3)
            {
                double refLon = gLon[q[t]];
                bool inD = false;
                for (int j = 0; j < 3; j++)
                {
                    int id = q[t + j], n = world.N++;
                    double lo = refLon + Wrap(gLon[id] - refLon);
                    wp[n] = gPos[id]; wc[n] = gShade[id].WithAlpha(fa); rc[n] = gRim[id];
                    wtx[n] = new SKPoint((float)((lo - wt.Lon0) * wt.Ppd), (float)((wt.Lat1 - gLat[id]) * wt.Ppd));
                    if (dtl != null && gLat[id] > dtl.Lat0 && gLat[id] < dtl.Lat1 && gLon[id] > dtl.Lon0 && gLon[id] < dtl.Lon1) inD = true;
                }
                if (inD && detailOn > 0.01f)
                    for (int j = 0; j < 3; j++)
                    {
                        int id = q[t + j], n = detail.N++;
                        double edge = Math.Min(Math.Min(gLat[id] - dtl!.Lat0, dtl.Lat1 - gLat[id]), Math.Min(gLon[id] - dtl.Lon0, dtl.Lon1 - gLon[id]));
                        detail.Pos[n] = gPos[id]; detail.Col[n] = gShade[id].WithAlpha(B(fade * detailOn * SS(0, 2.0, edge)));
                        detail.Tex[n] = new SKPoint((float)((gLon[id] - dtl.Lon0) * dtl.Ppd), (float)((dtl.Lat1 - gLat[id]) * dtl.Ppd));
                    }
            }
        }
        int nWorld = world.N, nDetail = detail.N;
        world.End(tex: true, col2: true); detail.End(tex: true, col2: false);
        pt = Prof.Lap(PMesh, pt);
        // world
        worldPaint.Shader = worldSlot.Image(worldImg, SKShaderTileMode.Repeat, SKShaderTileMode.Clamp);
        worldPaint.FilterQuality = FQ;
        using (var verts = SKVertices.CreateCopy(SKVertexMode.Triangles, world.Pos, world.Tex, world.Col))
            cv.DrawVertices(verts, SKBlendMode.Modulate, worldPaint);
        // the sharper regional picture over it, fading in as we come down
        if (nDetail > 0 && detailImg != null)
        {
            detailPaint.Shader = detailSlot.Image(detailImg, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            detailPaint.FilterQuality = FQ;
            using var verts = SKVertices.CreateCopy(SKVertexMode.Triangles, detail.Pos, detail.Tex, detail.Col);
            cv.DrawVertices(verts, SKBlendMode.Modulate, detailPaint);
        }
        // cyan rim haze + a little sheen toward the light (same positions as the world triangles)
        if (nWorld > 0)
        {
            rimPaint.Shader = white;
            using var verts = SKVertices.CreateCopy(SKVertexMode.Triangles, world.Pos, world.Pos, world.Col2);
            cv.DrawVertices(verts, SKBlendMode.Modulate, rimPaint);
        }
        pt = Prof.Lap(PVerts, pt);
        // atmosphere beyond the horizon: the limbs of slightly bigger spheres, glowing toward the light
        DrawHalo(cv, c, cd, e1, e2, cl, fade);

        pt = Prof.Lap(PHalo, pt);
        // drifting soft lights in front (each light's gradient is built around (0,0) and moved with the canvas)
        for (int i = 0; i < lights.Length; i++)
        {
            var (fx, sp, fy, fr, fi) = lights[i];
            float bx = r.Left + (float)((fx + 0.018 * u * sp) % 1.0) * W, by = r.Top + (float)fy * H, br = (float)(H * fr);
            double gk = fi * 2.2 * fade;
            var col = new SKColor(B(0.25 * gk), B(0.55 * gk), B(0.6 * gk), 255);
            var half = new SKColor(B(0.12 * gk), B(0.27 * gk), B(0.3 * gk), 255);
            lightPaint.Shader = lightSlots[i].Radial(default, br, [col, half, SKColors.Black], Stops.At055);
            cv.Save(); cv.Translate(bx, by);
            cv.DrawCircle(0, 0, br, lightPaint);
            cv.Restore();
        }
        pt = Prof.Lap(PLights, pt);
        // film: vignette + grain
        vigPaint.Shader = vigSlot.Radial(new SKPoint(r.MidX, r.MidY), Math.Max(W, H) * 0.72f,
            [new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 20), new SKColor(0, 0, 0, 115)], Stops.At045);
        cv.DrawRect(r, vigPaint);
        grainBmp ??= BuildGrain();
        // the grain picture scrolls: the canvas moves instead of making a new shader with a new matrix each frame
        float gx = (float)(u * 977 % 256), gy = (float)(u * 631 % 256);
        grainPaint.Shader = grainSlot.Bitmap(grainBmp, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKMatrix.Identity);
        grainPaint.Color = SKColors.White.WithAlpha(B(0.55 * fade));
        cv.Save(); cv.Translate(gx, gy);
        cv.DrawRect(new SKRect(r.Left - gx, r.Top - gy, r.Right - gx, r.Bottom - gy), grainPaint);
        cv.Restore();

        pt = Prof.Lap(PFilm, pt);
        // ---- names and the pin
        taken.Clear();
        string place = PinName(s);
        bool pinVis = c.Project(Geo(tLat, tLon), out float px, out float py, out _);
        float R0 = H * 0.006f, ph = H * 0.07f * pin, hx = px, hy = py - ph;
        float pinTs = H * 0.024f;
        var tf = Fonts.CormorantGaramond.SemiBold;
        if (pinVis && pin > 0)
        {
            float tw = SpacedWidth(place, pinTs, pinTs * 0.5f);
            taken.Add(new SKRect(hx - R0 * 3, hy - pinTs, hx + R0 * 4.2f + tw, py + R0 * 4));
        }
        // countries: the big ones from far out, more as we come down
        double lim = 2.6 + 1.3 * SS(1.25, 0.35, c.Alt);   // big countries far out, a few more coming down; never the micro-states
        float cSize = H * 0.022f;
        foreach (var lb in countries!)
        {
            if (lb.Rank > lim + 0.6) break;
            float la = fade * Clamp01(lim - lb.Rank + 0.6) * SS(0.05, 0.12, c.Alt);
            if (la < 0.02f) continue;
            PlaceLabel(cv, c, lb, cSize, tf, la, taken);
        }
        float ra = fade * SS(0.30, 0.20, c.Alt);
        if (ra > 0.02f)
            foreach (var lb in regions) PlaceLabel(cv, c, lb, H * 0.016f, tf, ra, taken);
        if (pinVis && pin > 0) DrawPin(cv, px, py, hx, hy, R0, pin * fade, place, pinTs, tf);
        cv.RestoreToCount(save);
        Prof.Lap(PNames, pt);
        return true;
    }

    static double Wrap(double d) { while (d > 180) d -= 360; while (d < -180) d += 360; return d; }
    static byte B(double v) => (byte)Math.Clamp(v * 255, 0, 255);

    static readonly double[] haloRho = { 1.0, 1.006, 1.016, 1.035, 1.07 };
    static readonly double[] haloInten = { 1.0, 0.75, 0.42, 0.14, 0 };
    static readonly SKPoint[] haloPos = new SKPoint[5 * NA];
    static readonly bool[] haloOk = new bool[5 * NA];
    static readonly SKColor[] haloCol = new SKColor[5 * NA];
    static readonly SKPaint haloPaint = new() { BlendMode = SKBlendMode.Plus, IsAntialias = true };

    static readonly int PSpace = Prof.Slot("globe.space"), PMesh = Prof.Slot("globe.mesh"), PVerts = Prof.Slot("globe.verts"),
        PHalo = Prof.Slot("globe.halo"), PLights = Prof.Slot("globe.lights"), PFilm = Prof.Slot("globe.film"), PNames = Prof.Slot("globe.names");

    static void DrawHalo(SKCanvas cv, Cam c, V cd, V e1, V e2, double cl, float fade)
    {
        var rho = haloRho; var inten = haloInten;
        int nr = rho.Length;
        var pos = haloPos; var ok = haloOk; var col = haloCol;
        for (int k = 0; k < nr; k++)
        {
            double rr = rho[k]; if (rr >= cl) return;
            var cen = cd * (rr * rr / cl); double rad = rr * Math.Sqrt(1 - rr * rr / (cl * cl));
            for (int a = 0; a < NA; a++)
            {
                var p = cen + (e1 * cosA[a] + e2 * sinA[a]) * rad;
                int id = k * NA + a;
                ok[id] = c.Project(p, out float x, out float y, out _); pos[id] = new SKPoint(x, y);
                double lamh = Math.Clamp(p.N.Dot(c.L) * 0.5 + 0.5, 0, 1), g = inten[k] * (0.25 + 0.9 * lamh) * fade * 0.85;
                col[id] = new SKColor(B(0.30 * g), B(0.58 * g), B(0.64 * g), 255);
            }
        }
        var hb = halo;
        hb.Begin((nr - 1) * NA * 6);
        var vp = hb.Pos; var vc = hb.Col;
        for (int k = 0; k < nr - 1; k++)
            for (int a = 0; a < NA; a++)
            {
                int a1 = (a + 1) % NA, v0 = k * NA + a, v1 = k * NA + a1, v2 = (k + 1) * NA + a1, v3 = (k + 1) * NA + a;
                if (!ok[v0] || !ok[v1] || !ok[v2] || !ok[v3]) continue;
                int n = hb.N;
                vp[n] = pos[v0]; vc[n] = col[v0]; vp[n + 1] = pos[v1]; vc[n + 1] = col[v1]; vp[n + 2] = pos[v2]; vc[n + 2] = col[v2];
                vp[n + 3] = pos[v0]; vc[n + 3] = col[v0]; vp[n + 4] = pos[v2]; vc[n + 4] = col[v2]; vp[n + 5] = pos[v3]; vc[n + 5] = col[v3];
                hb.N = n + 6;
            }
        int count = hb.N;
        hb.End(tex: false, col2: false);
        if (count == 0) return;
        haloPaint.Shader = white;
        using var verts = SKVertices.CreateCopy(SKVertexMode.Triangles, hb.Pos, hb.Pos, hb.Col);
        cv.DrawVertices(verts, SKBlendMode.Modulate, haloPaint);
    }

    static SKBitmap BuildGrain()
    {
        // 2026-10-24: pixels written straight into the bitmap (SetPixel made a canvas per pixel: ~13 MB of
        // garbage). Same bytes: SetPixel on a premultiplied bitmap stores round(c * a / 255).
        var info = new SKImageInfo(256, 256, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bmp = new SKBitmap(info);
        var px = new byte[256 * 256 * 4];
        var rnd = new Random(11);
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                int v = rnd.Next(256);
                byte a = (byte)(Math.Abs(v - 128) / 128.0 * 22);
                byte cval = (byte)(v > 128 ? a : 0);
                byte pm = (byte)((cval * a + 127) / 255);
                int i = (y * 256 + x) * 4;
                px[i] = pm; px[i + 1] = pm; px[i + 2] = pm; px[i + 3] = a;
            }
        for (int y = 0; y < 256; y++) System.Runtime.InteropServices.Marshal.Copy(px, y * 256 * 4, bmp.GetPixels() + y * bmp.RowBytes, 256 * 4);
        bmp.NotifyPixelsChanged();
        bmp.SetImmutable();
        return bmp;   // kept alive in a static: a shader alone doesn't keep its bitmap from being collected
    }

    // the pin caption, made only when the track's names change
    static string? pinCity, pinBroadcast, pinCountry, pinName;
    static string PinName(RaceSnapshot s)
    {
        string? a = s.Extras.TrackCity, b = s.TrackBroadcastName, c = s.Extras.TrackCountry;
        if (pinName != null && ReferenceEquals(a, pinCity) && ReferenceEquals(b, pinBroadcast) && ReferenceEquals(c, pinCountry)) return pinName;
        var city = (a ?? "").Trim();
        if (city.Length == 0) city = (b ?? c ?? "").Trim();
        pinCity = a; pinBroadcast = b; pinCountry = c;
        return pinName = city.ToUpperInvariant();
    }

    static readonly SKPaint measure = new() { IsAntialias = true };
    static float SpacedWidth(string text, float size, float sp)
    {
        measure.Typeface = Fonts.CormorantGaramond.SemiBold; measure.TextSize = size;
        return TextBlobs.SpacedWidth(text, measure, sp);
    }

    static void DrawSpaced(SKCanvas cv, string text, float x, float baseline, float size, float sp, SKTypeface tf, SKColor col)
    {
        var p = textPaint;
        p.Typeface = tf; p.TextSize = size; p.Color = col;
        TextBlobs.DrawSpaced(cv, text, x, baseline, sp, p);
    }

    static void PlaceLabel(SKCanvas cv, Cam c, Label lb, float size, SKTypeface tf, float a, List<SKRect> taken)
    {
        var g = Geo(lb.Lat, lb.Lon);
        if (!c.Project(g, out float x, out float y, out _)) return;
        double facing = (c.C - g).N.Dot(g);
        a *= SS(0.22, 0.5, facing);
        if (a < 0.02f) return;
        string text = lb.Upper;
        float sp = size * 0.55f, tw = SpacedWidth(text, size, sp);
        var box = new SKRect(x - tw / 2 - size * 0.4f, y - size * 0.75f, x + tw / 2 + size * 0.4f, y + size * 0.75f);
        if (!c.Rect.Contains(box)) return;
        foreach (var t in taken) if (t.IntersectsWith(box)) return;
        taken.Add(box);
        DrawSpaced(cv, text, x - tw / 2, y + size * 0.33f, size, sp, tf, new SKColor(205, 215, 228, B(0.6 * a)));
    }

    static void DrawPin(SKCanvas cv, float px, float py, float hx, float hy, float R, float a, string name, float ts, SKTypeface tf)
    {
        var p = pinPaint;
        // glow on the map
        cv.Save(); cv.Translate(px, py); cv.Scale(1, 0.75f);
        p.Reset(); p.IsAntialias = true;
        p.Shader = pinGlowSlot.Radial(new SKPoint(0, 0), R * 6, [new SKColor(255, 40, 40, B(0.75 * a)), new SKColor(255, 40, 40, B(0.28 * a)), new SKColor(255, 40, 40, 0)], Stops.At035);
        cv.DrawCircle(0, 0, R * 6, p);
        p.Shader = null;
        p.Color = new SKColor(255, 90, 80, B(a)); cv.DrawCircle(0, 0, R, p);
        p.Color = new SKColor(255, 225, 215, B(a)); cv.DrawCircle(0, 0, R * 0.45f, p);
        cv.Restore();
        // stem and head
        p.Reset(); p.IsAntialias = true; p.Color = new SKColor(230, 60, 55, B(0.86 * a)); p.StrokeWidth = Math.Max(2, R * 0.45f); p.StrokeCap = SKStrokeCap.Round;
        cv.DrawLine(px, py, hx, hy + R * 2.2f, p);
        p.Reset(); p.IsAntialias = true; p.Color = new SKColor(225, 40, 40, B(0.94 * a));
        cv.DrawCircle(hx, hy, R * 2.4f, p);
        p.Reset(); p.IsAntialias = true; p.Style = SKPaintStyle.Stroke; p.StrokeWidth = Math.Max(2, R * 0.4f); p.Color = new SKColor(255, 255, 255, B(0.9 * a));
        cv.DrawCircle(hx, hy, R * 2.4f, p);
        DrawSpaced(cv, name, hx + R * 4.2f, hy + ts * 0.33f, ts, ts * 0.5f, tf, new SKColor(240, 244, 250, B(0.92 * a)));
    }

    static readonly SKPaint warmPaint = new() { Color = SKColors.White.WithAlpha(1), FilterQuality = SKFilterQuality.Medium };
    static float warmedH = -1;

    /// <summary>Touch both pictures with an invisible 2-pixel draw so the graphics card has them uploaded
    /// before the globe beat starts (avoids a first-frame hitch). <paramref name="r"/> = the rect the beat
    /// will fill: the place names' text is laid out for its size here too, once.</summary>
    public static void Warm(SKCanvas cv, SKRect r)
    {
        PinPictures();
        var dot = new SKRect(r.Left, r.Top, r.Left + 2, r.Top + 2);
        if (worldImg != null) { warmPaint.Shader = warmWorld.Image(worldImg, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp); cv.DrawRect(dot, warmPaint); }
        if (detailImg != null) { warmPaint.Shader = warmDetail.Image(detailImg, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp); cv.DrawRect(dot, warmPaint); }
        warmPaint.Shader = null;
        grainBmp ??= BuildGrain();
        if (!sizing && sizedFor != r.Height && key != null)
        {
            sizing = true; sizedFor = r.Height;
            var rr = r;
            Task.Run(() =>
            {
                try { int cap = (int)(MaxQuads(rr) * 6 * 1.1) + 6; sizedBufs = new[] { new VBuf(cap), new VBuf(cap) }; }
                catch { }
                finally { sizing = false; }
            });
        }
        // 2026-10-24: lay out the place names for this screen size before the beat (their text blobs)
        // a dozen names per call (Warm runs every frame of the pages before the beat), so it never makes a hitch
        if (countries == null || key == null) return;
        if (warmedH != r.Height || !ReferenceEquals(warmedKey, key)) { warmedH = r.Height; warmedKey = key; warmIdx = 0; }
        var tf = Fonts.CormorantGaramond.SemiBold;
        float H = r.Height;
        var regs = regions;
        for (int n = 0; n < 12; n++, warmIdx++)
        {
            int i = warmIdx;
            if (i < countries.Count)
            {
                if (countries[i].Rank > 2.6 + 1.3 + 0.6) { warmIdx = countries.Count - 1; continue; }   // never drawn
                Prep(countries[i].Upper, H * 0.022f, tf);
            }
            else if (i - countries.Count < regs.Count) Prep(regs[i - countries.Count].Upper, H * 0.016f, tf);
            else return;
        }
    }
    static string? warmedKey; static int warmIdx;
    static void Prep(string text, float size, SKTypeface tf)
    {
        float sp = size * 0.55f;
        SpacedWidth(text, size, sp);
        textPaint.Typeface = tf; textPaint.TextSize = size;
        TextBlobs.SpacedWidth(text, textPaint, sp);   // builds the drawing paint's blob too
    }
}
