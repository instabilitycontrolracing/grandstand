using System.IO;
using System.Net.Http;
using System.Text.Json;
using SkiaSharp;
using ApexHUD.Native.Common;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// The ground under the 3D track map (2026-10-25, Angelo: "how do we make it have more like terrain features but not like
/// a shitty satellite map" - and yes to downloading the free map data). Built once per track in the background and saved:
/// real elevation (the free Terrarium elevation tiles) as soft hill shading and thin contour lines, and from OpenStreetMap
/// the woods, lakes, rivers, built-up areas and main roads, all drawn flat in the overlay's own style - no photos.
///
/// The picture is lined up with Grandstand's own outline of the circuit (Documents\iRacing\ApexHUD\maps) by fitting that
/// outline onto the real roads around iRacing's track position (rotation, mirror, position, scale). If the fit isn't
/// convincing, no ground is shown - never a wrong one. The result is stored in map units, so it zooms and flies with the
/// track card's camera, and is drawn as light and shade over the card's own colour (it suits every card colour).
/// Cache: Documents\iRacing\Grandstand\terrain\&lt;map&gt;.png + .json
/// </summary>
public static class TrackTerrain
{
    public sealed record Data(SKBitmap Ground, float U0, float V0, float K)
    {
        /// <summary>Ground picture pixels -> map units.</summary>
        public SKMatrix ToMap => SKMatrix.CreateScaleTranslation(1f / K, 1f / K, U0, V0);
        /// <summary>0..1 over the first 1.5 s after it arrived, so ground that turns up mid-card fades in.</summary>
        public float FadeIn => (float)Math.Clamp((DateTime.UtcNow - LoadedAt).TotalSeconds / 1.5, 0, 1);
        public DateTime LoadedAt { get; init; } = DateTime.UtcNow;
    }

    const int Version = 2;
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "terrain");
    static readonly Dictionary<string, Data?> loaded = new();
    static readonly HashSet<string> building = new();
    static readonly object gate = new();

    /// <summary>Starts building this track's ground as soon as the track is known (called every frame; cheap once known),
    /// so it's ready by the time the track card plays.</summary>
    public static void Prepare(RaceSnapshot s)
    {
        if (!s.IsConnected || string.IsNullOrEmpty(s.TrackRawName) || double.IsNaN(s.TrackLatitude)) return;
        if (string.Equals(s.TrackRawName, prepFor, StringComparison.Ordinal)) return;   // 2026-10-30: was ReferenceEquals - a new string each update logged "prepare" every few seconds
        prepFor = s.TrackRawName;
        var mb = TrackMapCache.Resolve(s.TrackConfigName, s.TrackDisplayName, s.TrackBroadcastName, s.TrackRawName);
        double len = TrackCorners.LengthM(s.Extras.TrackLength);
        Log($"terrain: prepare {s.TrackRawName} map={mb ?? "(none)"} at {s.TrackLatitude:0.0000},{s.TrackLongitude:0.0000} lap {len:0} m");
        if (mb != null) For(mb, Assets.Resolve("maps", mb + ".json"), s.TrackLatitude, s.TrackLongitude, len);
        else prepFor = null;                                               // no outline yet: try again later
    }
    static string? prepFor;

    /// <summary>The ground for this map, or null (not built yet / no data / fit not good enough). Starts the build when needed.</summary>
    public static Data? For(string mapBase, string mapJsonPath, double lat, double lon, double lapLenM)
    {
        if (double.IsNaN(lat) || double.IsNaN(lon) || lapLenM <= 0 || string.IsNullOrEmpty(mapBase) || (lat == 0 && lon == 0)) return null;
        lock (gate)
        {
            if (loaded.TryGetValue(mapBase, out var d)) return d;
            if (building.Contains(mapBase)) return null;
            building.Add(mapBase);
        }
        Log("terrain: building " + mapBase);
        Task.Run(() =>
        {
            Data? got = null;
            try { got = LoadOrBuild(mapBase, mapJsonPath, lat, lon, lapLenM); }
            catch (Exception ex) { Log("terrain: " + mapBase + " failed: " + ex.Message); }
            lock (gate) { loaded[mapBase] = got; building.Remove(mapBase); }
        });
        return null;
    }

    static void Log(string s) { try { OverlayLog.Write(s); } catch { } }

    static Data? LoadOrBuild(string mapBase, string mapJson, double lat, double lon, double lapLen)
    {
        string safe = string.Concat(mapBase.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        string png = Path.Combine(Dir, safe + ".png"), meta = Path.Combine(Dir, safe + ".json");
        if (File.Exists(meta))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(meta));
            var r = doc.RootElement;
            if (r.GetProperty("v").GetInt32() == Version)
            {
                if (!r.GetProperty("ok").GetBoolean())
                {
                    // a failed build is retried after a day (the map services may have been down)
                    if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(meta)).TotalHours < 24) return null;
                }
                else if (File.Exists(png))
                {
                    using var st = File.OpenRead(png);
                    var bmp = SKBitmap.Decode(st);
                    if (bmp != null) return new Data(bmp, r.GetProperty("u0").GetSingle(), r.GetProperty("v0").GetSingle(), r.GetProperty("k").GetSingle());
                }
            }
        }
        Directory.CreateDirectory(Dir);
        var built = Build(mapJson, lat, lon, lapLen, out string why);
        if (built == null)
        {
            File.WriteAllText(meta, JsonSerializer.Serialize(new { v = Version, ok = false, why }));
            Log($"terrain: {mapBase}: no ground ({why})");
            return null;
        }
        using (var img = SKImage.FromBitmap(built.Ground)) using (var enc = img.Encode(SKEncodedImageFormat.Png, 100)) File.WriteAllBytes(png, enc.ToArray());
        File.WriteAllText(meta, JsonSerializer.Serialize(new { v = Version, ok = true, u0 = built.U0, v0 = built.V0, k = built.K, why }));
        Log($"terrain: {mapBase}: built ({why})");
        return built;
    }

    // ------------------------------------------------------------------ the build
    static readonly HttpClient http = MakeHttp();
    static HttpClient MakeHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Grandstand-overlay/1.0 (personal iRacing broadcast overlay)");
        return h;
    }

    sealed class Way { public string Kind = ""; public List<(double Lat, double Lon)> Pts = new(); public bool Area; }

    static Data? Build(string mapJson, double lat0, double lon0, double lapLen, out string why)
    {
        // 1. the outline (map units) and how big it really is
        var P = new List<SKPoint>();
        using (var doc = JsonDocument.Parse(File.ReadAllText(mapJson)))
            foreach (var pt in doc.RootElement.GetProperty("points").EnumerateArray())
            {
                var q = new SKPoint(pt[0].GetSingle(), pt[1].GetSingle());
                if (P.Count == 0 || SKPoint.Distance(P[^1], q) > 0.01f) P.Add(q);
            }
        if (P.Count < 50) { why = "outline too short"; return null; }
        double per = 0; for (int i = 0; i < P.Count; i++) per += SKPoint.Distance(P[i], P[(i + 1) % P.Count]);
        double mPerUnit = lapLen / per;
        float minU = P.Min(p => p.X), maxU = P.Max(p => p.X), minV = P.Min(p => p.Y), maxV = P.Max(p => p.Y);
        double extM = Math.Max(maxU - minU, maxV - minV) * mPerUnit;
        double R = extM * 0.85 + 1200;                                   // metres either side of iRacing's track position
        const int N = 1600; double mpp = 2 * R / N;
        double kx = 111320 * Math.Cos(lat0 * Math.PI / 180), ky = 111320;
        double dLat = R / ky, dLon = R / kx;

        // 2. elevation tiles
        double tilePxM = 156543.03 * Math.Cos(lat0 * Math.PI / 180);
        int z = Math.Clamp((int)Math.Round(Math.Log2(tilePxM / mpp)), 9, 14);
        var elev = new float[N * N];
        {
            int n = 1 << z;
            double TX(double lon) => (lon + 180) / 360 * n;
            double TY(double lat) { double r = lat * Math.PI / 180; return (1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2 * n; }
            int tx0 = (int)TX(lon0 - dLon), tx1 = (int)TX(lon0 + dLon), ty0 = (int)TY(lat0 + dLat), ty1 = (int)TY(lat0 - dLat);
            if ((tx1 - tx0 + 1) * (ty1 - ty0 + 1) > 64) { why = "area too large"; return null; }
            int MW = (tx1 - tx0 + 1) * 256, MH = (ty1 - ty0 + 1) * 256;
            var mos = new float[MW * MH];
            for (int tx = tx0; tx <= tx1; tx++)
                for (int ty = ty0; ty <= ty1; ty++)
                {
                    var bytes = http.GetByteArrayAsync($"https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{tx}/{ty}.png").GetAwaiter().GetResult();
                    using var tb = SKBitmap.Decode(bytes);
                    if (tb == null) continue;
                    for (int y = 0; y < 256; y++)
                        for (int x = 0; x < 256; x++)
                        {
                            var c = tb.GetPixel(x, y);
                            mos[((ty - ty0) * 256 + y) * MW + (tx - tx0) * 256 + x] = c.Red * 256f + c.Green + c.Blue / 256f - 32768f;
                        }
                }
            for (int i = 0; i < N; i++)
                for (int j = 0; j < N; j++)
                {
                    double X = j * mpp - R, Y = R - i * mpp;
                    double lon = lon0 + X / kx, lat = lat0 + Y / ky;
                    double ex = TX(lon) * 256 - tx0 * 256, ey = TY(lat) * 256 - ty0 * 256;
                    elev[i * N + j] = Bilinear(mos, MW, MH, (float)ex, (float)ey);
                }
            Blur(elev, N, N, 2);
        }

        // 3. map features
        double s_ = lat0 - dLat, w_ = lon0 - dLon, n_ = lat0 + dLat, e_ = lon0 + dLon;
        string bb = FormattableString.Invariant($"{s_:0.00000},{w_:0.00000},{n_:0.00000},{e_:0.00000}");
        string oq = "[out:json][timeout:90];(" +
                   $"way[\"highway\"~\"^(motorway|trunk|primary|secondary|tertiary|raceway)$\"]({bb});" +
                   $"way[\"natural\"~\"^(wood|water)$\"]({bb});way[\"landuse\"~\"^(forest|residential|industrial|commercial|retail|reservoir|basin)$\"]({bb});" +
                   $"way[\"waterway\"~\"^(river|canal)$\"]({bb});relation[\"natural\"~\"^(wood|water)$\"]({bb});relation[\"landuse\"=\"forest\"]({bb});" +
                   ");out geom;";
        string osm;
        using (var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", oq) }))
            osm = http.PostAsync("https://overpass-api.de/api/interpreter", content).GetAwaiter().GetResult().EnsureSuccessStatusCode().Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var ways = new List<Way>();
        using (var doc = JsonDocument.Parse(osm))
            foreach (var el in doc.RootElement.GetProperty("elements").EnumerateArray())
            {
                string kind = "";
                if (el.TryGetProperty("tags", out var t))
                {
                    string G(string k) => t.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
                    string nat = G("natural"), lu = G("landuse"), hw = G("highway"), ww = G("waterway");
                    kind = nat == "water" || lu is "reservoir" or "basin" ? "water"
                         : nat == "wood" || lu == "forest" ? "wood"
                         : lu is "residential" or "industrial" or "commercial" or "retail" ? "urban"
                         : ww.Length > 0 ? "river"
                         : hw == "raceway" ? "race"
                         : hw is "motorway" or "trunk" or "primary" ? "major"
                         : hw.Length > 0 ? "road" : "";
                }
                if (kind.Length == 0) continue;
                void AddGeom(JsonElement g, bool area)
                {
                    var w = new Way { Kind = kind, Area = area };
                    foreach (var p in g.EnumerateArray()) w.Pts.Add((p.GetProperty("lat").GetDouble(), p.GetProperty("lon").GetDouble()));
                    if (w.Pts.Count >= 2) ways.Add(w);
                }
                bool isArea = kind is "water" or "wood" or "urban";
                if (el.TryGetProperty("geometry", out var geo)) AddGeom(geo, isArea);
                else if (el.TryGetProperty("members", out var mem))
                    foreach (var m in mem.EnumerateArray())
                        if (m.TryGetProperty("role", out var role) && role.GetString() == "outer" && m.TryGetProperty("geometry", out var mg)) AddGeom(mg, isArea);
            }
        SKPoint ToC(double lat, double lon) => new((float)(((lon - lon0) * kx + R) / mpp), (float)((R - (lat - lat0) * ky) / mpp));

        // 4. fit the outline onto the roads: distance to the nearest raceway / main road
        var dist = new float[N * N];
        {
            using var mb = new SKBitmap(new SKImageInfo(N, N, SKColorType.Alpha8));
            using (var mc = new SKCanvas(mb))
            using (var rp = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = SKColors.White })
            {
                mc.Clear(SKColors.Transparent);
                foreach (var w in ways)
                {
                    if (w.Kind is not ("race" or "major" or "road")) continue;
                    using var pth = new SKPath();
                    for (int i = 0; i < w.Pts.Count; i++) { var c = ToC(w.Pts[i].Lat, w.Pts[i].Lon); if (i == 0) pth.MoveTo(c); else pth.LineTo(c); }
                    mc.DrawPath(pth, rp);
                }
            }
            var px = mb.GetPixelSpan();
            for (int i = 0; i < N * N; i++) dist[i] = px[i] > 0 ? 0 : 1e9f;
            Chamfer(dist, N, N);
        }
        var race = ways.Where(w => w.Kind == "race").SelectMany(w => w.Pts).ToList();
        SKPoint centre = race.Count > 20 ? ToC(race.Average(p => p.Lat), race.Average(p => p.Lon)) : new SKPoint(N / 2f, N / 2f);
        float cu = P.Average(p => p.X), cv = P.Average(p => p.Y);
        float sBase = (float)(mPerUnit / mpp);
        var sub = new List<SKPoint>(); for (int i = 0; i < P.Count; i += Math.Max(1, P.Count / 220)) sub.Add(new SKPoint((P[i].X - cu) * sBase, (P[i].Y - cv) * sBase));
        float Score(float ang, int flip, float sc, float tx, float ty, List<SKPoint> pts)
        {
            float ca = MathF.Cos(ang), sa = MathF.Sin(ang); double sum = 0; int n = 0;
            foreach (var p in pts)
            {
                float x = p.X * sc, y = p.Y * sc * flip;
                int X = (int)(x * ca - y * sa + tx), Y = (int)(x * sa + y * ca + ty);
                if (X < 0 || Y < 0 || X >= N || Y >= N) { sum += 30; n++; continue; }
                sum += Math.Min(dist[Y * N + X], 30f); n++;
            }
            return (float)(sum / Math.Max(1, n));
        }
        (float S, float A, int F, float Sc, float Tx, float Ty) best = (1e9f, 0, 1, 1, 0, 0);
        float span = (float)(R * 0.45 / mpp);
        foreach (int flip in new[] { 1, -1 })
            for (int deg = 0; deg < 360; deg += 2)
            {
                float ang = deg * MathF.PI / 180;
                for (float tx = centre.X - span; tx <= centre.X + span; tx += 24)
                    for (float ty = centre.Y - span; ty <= centre.Y + span; ty += 24)
                    {
                        float sc0 = Score(ang, flip, 1, tx, ty, sub);
                        if (sc0 < best.S) best = (sc0, ang, flip, 1, tx, ty);
                    }
            }
        for (int it = 0; it < 4; it++)
        {
            var b0 = best;
            for (float da = -2f; da <= 2f; da += 0.5f)
                foreach (float ds in new[] { 0.97f, 0.985f, 1f, 1.015f, 1.03f })
                    for (float dx = -24; dx <= 24; dx += 4)
                        for (float dy = -24; dy <= 24; dy += 4)
                        {
                            float a = b0.A + da * MathF.PI / 180, sc = b0.Sc * ds;
                            float v = Score(a, b0.F, sc, b0.Tx + dx, b0.Ty + dy, sub);
                            if (v < best.S) best = (v, a, b0.F, sc, b0.Tx + dx, b0.Ty + dy);
                        }
        }
        double metresOff = best.S * mpp;
        why = FormattableString.Invariant($"fit {metresOff:0} m mean, rot {best.A * 180 / Math.PI:0}, mirror {best.F < 0}, zoom {z}");
        if (metresOff > 55) { why = "outline didn't match the roads (" + why + ")"; return null; }

        // map units -> canvas px
        var F = SKMatrix.CreateTranslation(-cu, -cv)
            .PostConcat(SKMatrix.CreateScale(sBase * best.Sc, sBase * best.Sc * best.F))
            .PostConcat(SKMatrix.CreateRotation(best.A))
            .PostConcat(SKMatrix.CreateTranslation(best.Tx, best.Ty));

        // 5. draw the ground in MAP units: the outline's box with room around it
        float mU = (maxU - minU) * 0.55f, mV = (maxV - minV) * 0.55f;
        float u0 = minU - mU, v0 = minV - mV, uw = maxU - minU + 2 * mU, vh = maxV - minV + 2 * mV;
        float k = 1500f / Math.Max(uw, vh);
        int W = (int)(uw * k), H = (int)(vh * k);
        var toOut = SKMatrix.CreateScaleTranslation(k, k, -u0 * k, -v0 * k);   // map -> out px
        F.TryInvert(out var Finv);
        var canvasToOut = Finv.PostConcat(toOut);
        toOut.TryInvert(out var outToMap);
        var outToCanvas = outToMap.PostConcat(F);
        double outMpp = mpp * (sBase * best.Sc) / k;

        // elevation on the output grid
        var E = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var cpt = outToCanvas.MapPoint(x, y);
                E[y * W + x] = Bilinear(elev, N, N, cpt.X, cpt.Y);
            }
        var Es = (float[])E.Clone(); Blur(Es, W, H, 4);
        float emin = Es.Min(), emax = Es.Max(), erng = Math.Max(1f, emax - emin);

        var outBmp = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var oc = new SKCanvas(outBmp);
        oc.Clear(SKColors.Transparent);

        // hill shading: light from the north-west; highlights white, shadows navy-black, on a transparent ground
        {
            using var shade = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            var buf = new byte[W * H * 4];
            double zf = 3.0, az = 315 * Math.PI / 180, alt = 45 * Math.PI / 180, sAlt = Math.Sin(alt);
            for (int y = 1; y < H - 1; y++)
                for (int x = 1; x < W - 1; x++)
                {
                    double gx = (Es[y * W + x + 1] - Es[y * W + x - 1]) * zf / (2 * outMpp);
                    double gy = (Es[(y - 1) * W + x] - Es[(y + 1) * W + x]) * zf / (2 * outMpp);   // +y = north
                    double slope = Math.Atan(Math.Sqrt(gx * gx + gy * gy)), aspect = Math.Atan2(-gx, gy);
                    double hs = Math.Clamp(sAlt * Math.Cos(slope) + Math.Cos(alt) * Math.Sin(slope) * Math.Cos(az - aspect), 0, 1) - sAlt;
                    double hn = (Es[y * W + x] - emin) / erng;
                    int o = (y * W + x) * 4;
                    if (hs >= 0) { buf[o] = 225; buf[o + 1] = 235; buf[o + 2] = 255; buf[o + 3] = (byte)Math.Clamp(hs * 260 + hn * 26, 0, 120); }
                    else { buf[o] = 2; buf[o + 1] = 6; buf[o + 2] = 18; buf[o + 3] = (byte)Math.Clamp(-hs * 330, 0, 150); }
                }
            System.Runtime.InteropServices.Marshal.Copy(buf, 0, shade.GetPixels(), buf.Length);
            oc.DrawBitmap(shade, 0, 0);
        }

        // features, drawn in canvas space through the fit
        oc.Save(); oc.SetMatrix(canvasToOut);
        float cs = (float)(1 / (canvasToOut.ScaleX == 0 ? 1 : Math.Sqrt(canvasToOut.ScaleX * canvasToOut.ScaleX + canvasToOut.SkewY * canvasToOut.SkewY)));
        void Areas(string kind, SKColor col, float blur)
        {
            using var p = new SKPaint { IsAntialias = true, Color = col, MaskFilter = blur > 0 ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur * cs) : null };
            using var pth = new SKPath { FillType = SKPathFillType.Winding };
            foreach (var w in ways)
            {
                if (w.Kind != kind || w.Pts.Count < 3) continue;
                for (int i = 0; i < w.Pts.Count; i++) { var c = ToC(w.Pts[i].Lat, w.Pts[i].Lon); if (i == 0) pth.MoveTo(c); else pth.LineTo(c); }
                pth.Close();
            }
            oc.DrawPath(pth, p);
        }
        void Lines(string kind, SKColor col, float width)
        {
            using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width * cs, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = col };
            using var pth = new SKPath();
            foreach (var w in ways)
            {
                if (w.Kind != kind) continue;
                for (int i = 0; i < w.Pts.Count; i++) { var c = ToC(w.Pts[i].Lat, w.Pts[i].Lon); if (i == 0) pth.MoveTo(c); else pth.LineTo(c); }
            }
            oc.DrawPath(pth, p);
        }
        Areas("urban", new SKColor(150, 170, 220, 34), 2.5f);
        Areas("wood", new SKColor(28, 86, 84, 128), 3f);
        Areas("water", new SKColor(10, 48, 108, 235), 0.8f);
        Lines("river", new SKColor(46, 104, 180, 190), 2.6f);
        Lines("major", new SKColor(210, 222, 248, 46), 1.6f);
        oc.Restore();

        // contour lines every 5 m, a stronger one every 20 m
        {
            var cbuf = new byte[W * H * 4];
            for (int y = 0; y < H - 1; y++)
                for (int x = 0; x < W - 1; x++)
                {
                    float e0 = Es[y * W + x], e1 = Es[y * W + x + 1], e2 = Es[(y + 1) * W + x];
                    int l0 = (int)Math.Floor(e0 / 5), l1 = (int)Math.Floor(e1 / 5), l2 = (int)Math.Floor(e2 / 5);
                    if (l0 == l1 && l0 == l2) continue;
                    int lv = Math.Max(l0, Math.Max(l1, l2));
                    bool major = lv % 4 == 0;
                    int o = (y * W + x) * 4;
                    cbuf[o] = 210; cbuf[o + 1] = 225; cbuf[o + 2] = 255; cbuf[o + 3] = (byte)(major ? 58 : 24);
                }
            using var cb = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            System.Runtime.InteropServices.Marshal.Copy(cbuf, 0, cb.GetPixels(), cbuf.Length);
            oc.DrawBitmap(cb, 0, 0);
        }

        // soft edges all round, so the ground melts into the card (each side fades over 18 %)
        foreach (var (p0, p1) in new[] { (new SKPoint(0, 0), new SKPoint(W * 0.18f, 0)), (new SKPoint(W, 0), new SKPoint(W * 0.82f, 0)),
                                         (new SKPoint(0, 0), new SKPoint(0, H * 0.18f)), (new SKPoint(0, H), new SKPoint(0, H * 0.82f)) })
            using (var fade = new SKPaint { BlendMode = SKBlendMode.DstIn, Shader = SKShader.CreateLinearGradient(p0, p1, new[] { SKColors.Transparent, SKColors.White }, null, SKShaderTileMode.Clamp) })
                oc.DrawRect(0, 0, W, H, fade);
        oc.Flush();
        return new Data(outBmp, u0, v0, k);
    }

    static float Bilinear(float[] a, int w, int h, float x, float y)
    {
        x = Math.Clamp(x, 0, w - 1.001f); y = Math.Clamp(y, 0, h - 1.001f);
        int x0 = (int)x, y0 = (int)y; float fx = x - x0, fy = y - y0;
        float v00 = a[y0 * w + x0], v10 = a[y0 * w + x0 + 1], v01 = a[(y0 + 1) * w + x0], v11 = a[(y0 + 1) * w + x0 + 1];
        return (v00 * (1 - fx) + v10 * fx) * (1 - fy) + (v01 * (1 - fx) + v11 * fx) * fy;
    }

    /// <summary>Box blur, three passes (close to a gaussian).</summary>
    static void Blur(float[] a, int w, int h, int r)
    {
        var t = new float[a.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            for (int y = 0; y < h; y++)
            {
                double acc = 0; int n = 0;
                for (int x = -r; x < w; x++)
                {
                    int xa = x + r; if (xa < w) { acc += a[y * w + xa]; n++; }
                    int xr = x - r - 1; if (xr >= 0) { acc -= a[y * w + xr]; n--; }
                    if (x >= 0) t[y * w + x] = (float)(acc / n);
                }
            }
            for (int x = 0; x < w; x++)
            {
                double acc = 0; int n = 0;
                for (int y = -r; y < h; y++)
                {
                    int ya = y + r; if (ya < h) { acc += t[ya * w + x]; n++; }
                    int yr = y - r - 1; if (yr >= 0) { acc -= t[yr * w + x]; n--; }
                    if (y >= 0) a[y * w + x] = (float)(acc / n);
                }
            }
        }
    }

    /// <summary>Distance (in pixels, 3-4 chamfer) to the nearest zero cell.</summary>
    static void Chamfer(float[] d, int w, int h)
    {
        const float A = 1f, B = 1.4142f;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float v = d[y * w + x];
                if (x > 0) v = Math.Min(v, d[y * w + x - 1] + A);
                if (y > 0)
                {
                    v = Math.Min(v, d[(y - 1) * w + x] + A);
                    if (x > 0) v = Math.Min(v, d[(y - 1) * w + x - 1] + B);
                    if (x < w - 1) v = Math.Min(v, d[(y - 1) * w + x + 1] + B);
                }
                d[y * w + x] = v;
            }
        for (int y = h - 1; y >= 0; y--)
            for (int x = w - 1; x >= 0; x--)
            {
                float v = d[y * w + x];
                if (x < w - 1) v = Math.Min(v, d[y * w + x + 1] + A);
                if (y < h - 1)
                {
                    v = Math.Min(v, d[(y + 1) * w + x] + A);
                    if (x < w - 1) v = Math.Min(v, d[(y + 1) * w + x + 1] + B);
                    if (x > 0) v = Math.Min(v, d[(y + 1) * w + x - 1] + B);
                }
                d[y * w + x] = v;
            }
    }

    /// <summary>The perspective that maps rectangle <paramref name="src"/> onto the four corners given (TL, TR, BR, BL).</summary>
    public static SKMatrix Perspective(SKRect src, SKPoint tl, SKPoint tr, SKPoint br, SKPoint bl)
    {
        var s = new[] { new SKPoint(src.Left, src.Top), new SKPoint(src.Right, src.Top), new SKPoint(src.Right, src.Bottom), new SKPoint(src.Left, src.Bottom) };
        var d = new[] { tl, tr, br, bl };
        var M = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double x = s[i].X, y = s[i].Y, u = d[i].X, v = d[i].Y;
            double[] r1 = { x, y, 1, 0, 0, 0, -u * x, -u * y, u }, r2 = { 0, 0, 0, x, y, 1, -v * x, -v * y, v };
            for (int j = 0; j < 9; j++) { M[2 * i, j] = r1[j]; M[2 * i + 1, j] = r2[j]; }
        }
        for (int c = 0; c < 8; c++)                                       // Gauss-Jordan
        {
            int piv = c; for (int r = c + 1; r < 8; r++) if (Math.Abs(M[r, c]) > Math.Abs(M[piv, c])) piv = r;
            for (int j = 0; j < 9; j++) (M[c, j], M[piv, j]) = (M[piv, j], M[c, j]);
            double dv = M[c, c]; if (Math.Abs(dv) < 1e-12) return SKMatrix.Identity;
            for (int j = 0; j < 9; j++) M[c, j] /= dv;
            for (int r = 0; r < 8; r++) if (r != c) { double f = M[r, c]; for (int j = 0; j < 9; j++) M[r, j] -= f * M[c, j]; }
        }
        return new SKMatrix((float)M[0, 8], (float)M[1, 8], (float)M[2, 8], (float)M[3, 8], (float)M[4, 8], (float)M[5, 8], (float)M[6, 8], (float)M[7, 8], 1f);
    }
}
