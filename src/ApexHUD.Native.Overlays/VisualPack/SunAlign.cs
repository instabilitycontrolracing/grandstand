using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>What kind of camera is on air, as far as the sun is concerned.</summary>
public enum CamMount { Unknown, Trackside, Cockpit, Forward, Rearward }

/// <summary>
/// W11 round 2 (2026-10-27, Angelo: lens effects "as long as timing is perfect and it lines up perfectly"). The overlay
/// cannot see the picture, so the flare is placed from geometry, and ONLY where the geometry is known:
/// <list type="bullet">
/// <item>The sun: iRacing's own SolarAltitude / SolarAzimuth telemetry (the sim's sun - not a computed one, so the time
/// zone and date can't be wrong). No sim sun = no flare.</item>
/// <item>The camera: only car-mounted cameras that look along the car - chase / far chase / nose / gyro (forward), rear
/// chase / gearbox (backward). Cockpit / roll bar views are excluded (same rule as the lens rain: the car's own windscreen);
/// trackside TV, pit lane, blimp and helicopter cameras point wherever the sim's camera editor says - unknown, so no flare
/// and no glow there.</item>
/// <item>The heading: the player's own car (live, not a replay) = YawNorth (checked against GPS: 0.0 deg mean error).
/// Any other car = the direction of Grandstand's track outline at the car's lap position, turned to true north by a
/// calibration learned while Angelo drives (his YawNorth against the outline, kept per track in
/// Documents\iRacing\Grandstand\sun_align.json) or, before that exists, by the terrain fit (TrackTerrain's rotation /
/// mirror). Checked on a Le Mans .ibt: outline heading vs YawNorth 1.1 deg median, 7 deg at the 90th percentile (the
/// corners) - so the flare is only shown where the road runs straight (the outline turns less than 6 deg over +-35 m).</item>
/// </list>
/// Field of view and pitch are not in the telemetry: per-mount defaults (chase 70 deg wide, pitched 4 deg down) that can be
/// tuned in control.json (visuals.lensFov / lensPitch) after one look on the TV.
/// </summary>
public sealed class SunAlign
{
    static readonly object FileGate = new();
    public static readonly string File = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "sun_align.json");

    /// <summary>Horizontal field of view (degrees) used for car-mounted cameras; 0 = per-mount default.</summary>
    public float FovOverrideDeg;
    /// <summary>Camera pitch (degrees, negative = looking down); NaN = per-mount default.</summary>
    public float PitchOverrideDeg = float.NaN;

    // ---- calibration (map outline bearing -> true bearing): true = sign * mapBearing + offset
    string mapKey = "";
    VpTrack? track;
    VpTrackPath? path;
    int calSign; double calOffset; string calSource = "none";
    // live learning (sums of unit vectors of the residual for both signs)
    double sP, cP, sM, cM; int nLearn; double lastSave = -1e12; int savedN;

    /// <summary>Where the heading came from last tick (for the log / notes).</summary>
    public string HeadingSource { get; private set; } = "none";
    public string CalibrationSource => calSource;

    /// <summary>The camera mount for a group / camera name (event time: called on a camera cut, allocates a lower-case copy).</summary>
    public static CamMount Classify(string groupName, string cameraName)
    {
        string g = (groupName ?? "").ToLowerInvariant(), c = (cameraName ?? "").ToLowerInvariant();
        string all = g + " | " + c;
        if (all.Trim(' ', '|').Length == 0) return CamMount.Unknown;
        if (Has(all, "cockpit") || Has(all, "in car") || Has(all, "incar") || Has(all, "roll bar") || Has(all, "rollbar")) return CamMount.Cockpit;
        if (Has(all, "rear chase") || Has(all, "gearbox") || Has(all, "rear view")) return CamMount.Rearward;
        if (Has(all, "chase") || Has(all, "nose") || Has(all, "gyro")) return CamMount.Forward;
        if (Has(all, "susp") || Has(all, "wing") || Has(all, "onboard")) return CamMount.Unknown;   // direction varies by car
        return CamMount.Trackside;
    }
    static bool Has(string s, string w) => s.Contains(w, StringComparison.Ordinal);

    public static (float HFov, float Pitch) MountDefaults(CamMount m) => m switch
    {
        CamMount.Forward => (70f, -4f),
        CamMount.Rearward => (70f, -4f),
        _ => (70f, 0f),
    };

    /// <summary>The track outline changed (or first use): load the calibration for it.</summary>
    public void SetTrack(string key, VpTrack? t)
    {
        if (key == mapKey && ReferenceEquals(t, track)) return;
        mapKey = key; track = t;
        path?.Dispose(); path = t != null ? new VpTrackPath(t.PathIn(new SKRect(0, 0, 1000, 1000))) : null;
        sP = cP = sM = cM = 0; nLearn = 0; savedN = 0;
        calSign = 0; calOffset = 0; calSource = "none";
        if (t == null || key.Length == 0) return;
        try
        {
            if (System.IO.File.Exists(File) && JsonNode.Parse(System.IO.File.ReadAllText(File)) is JsonObject root && root[key] is JsonObject o
                && o["sign"]?.GetValue<int>() is int sg && o["offsetDeg"]?.GetValue<double>() is double od)
            { calSign = sg; calOffset = od * Math.PI / 180; calSource = "learned"; savedN = o["n"]?.GetValue<int>() ?? 0; return; }
        }
        catch { }
        // the terrain fit: "rot 91, mirror False" (map units -> north-up web map)
        try
        {
            string safe = string.Concat(key.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            string tj = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "terrain", safe + ".json");
            if (System.IO.File.Exists(tj) && JsonNode.Parse(System.IO.File.ReadAllText(tj)) is JsonObject tr && tr["ok"]?.GetValue<bool>() == true
                && tr["why"]?.GetValue<string>() is string why)
            {
                int ri = why.IndexOf("rot ", StringComparison.Ordinal), mi = why.IndexOf("mirror ", StringComparison.Ordinal);
                if (ri >= 0 && mi >= 0)
                {
                    var num = why.AsSpan(ri + 4); int e = 0; while (e < num.Length && (char.IsDigit(num[e]) || num[e] == '-' || num[e] == '.')) e++;
                    if (double.TryParse(num[..e], NumberStyles.Float, CultureInfo.InvariantCulture, out var rot))
                    {
                        bool mirror = why.AsSpan(mi + 7).StartsWith("True", StringComparison.OrdinalIgnoreCase);
                        double a = rot * Math.PI / 180;
                        if (!mirror) { calSign = 1; calOffset = a; } else { calSign = -1; calOffset = Math.PI + a; }
                        calSource = "terrain fit";
                    }
                }
            }
        }
        catch { }
    }

    /// <summary>Bearing of the outline (map frame, clockwise from "up") at a lap fraction, and how much it turns over
    /// +-<paramref name="spanPct"/> (radians). False without an outline.</summary>
    public bool MapBearing(float pct, float spanPct, out double bearing, out double turn)
    {
        bearing = 0; turn = 0;
        if (path == null) return false;
        var t0 = path.TangentAt(pct);
        bearing = Math.Atan2(t0.X, -t0.Y);
        var ta = path.TangentAt(pct - spanPct); var tb = path.TangentAt(pct + spanPct);
        double ba = Math.Atan2(ta.X, -ta.Y), bb = Math.Atan2(tb.X, -tb.Y);
        turn = Math.Max(Math.Abs(Wrap(bearing - ba)), Math.Abs(Wrap(bb - bearing)));
        return true;
    }

    /// <summary>Learn the outline -> north turn from the player's own driving (live only). Allocation-free per tick;
    /// writes sun_align.json at most once a minute when it got better.</summary>
    public void Learn(float playerPct, double yawNorth, float speedMs, bool live, double nowMs)
    {
        if (!live || path == null || double.IsNaN(yawNorth) || speedMs < 25f || playerPct < 0) return;
        if (!MapBearing(playerPct, 0.0025f, out var mb, out var turn) || turn > 0.12) return;   // straights only
        double rp = yawNorth - mb, rm = yawNorth + mb;
        sP += Math.Sin(rp); cP += Math.Cos(rp); sM += Math.Sin(rm); cM += Math.Cos(rm);
        nLearn++;
        if (nLearn < 300) return;
        double RP = Math.Sqrt(sP * sP + cP * cP) / nLearn, RM = Math.Sqrt(sM * sM + cM * cM) / nLearn;
        double R = Math.Max(RP, RM);
        if (R < 0.97) return;                                 // not consistent enough (yet) - keep the old one
        calSign = RP >= RM ? 1 : -1;
        calOffset = calSign > 0 ? Math.Atan2(sP, cP) : Math.Atan2(sM, cM);
        calSource = "learned";
        if (nLearn > savedN * 2 && nowMs - lastSave > 60000)
        {
            lastSave = nowMs; savedN = nLearn;
            // the file is written off the render thread (it reads the other tracks' entries first)
            string key = mapKey; int sign = calSign, n = nLearn; double off = calOffset, rr = R;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    lock (FileGate)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                        var root = System.IO.File.Exists(File) ? JsonNode.Parse(System.IO.File.ReadAllText(File)) as JsonObject ?? new JsonObject() : new JsonObject();
                        root[key] = new JsonObject { ["sign"] = sign, ["offsetDeg"] = Math.Round(off * 180 / Math.PI, 2), ["n"] = n, ["r"] = Math.Round(rr, 4) };
                        System.IO.File.WriteAllText(File, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }
                }
                catch { }
            });
        }
    }

    /// <summary>The followed car's true heading (radians clockwise from north), or NaN when it isn't known well enough.
    /// <paramref name="turn"/> = how much the road turns around it (radians; 0 for the player's YawNorth).</summary>
    public double Heading(bool isPlayerLive, double yawNorth, float carPct, out double turn)
    {
        turn = 0;
        if (isPlayerLive && !double.IsNaN(yawNorth)) { HeadingSource = "YawNorth"; return yawNorth; }
        if (calSign == 0 || carPct < 0 || !MapBearing(carPct, 0.0025f, out var mb, out turn)) { HeadingSource = "unknown"; return double.NaN; }
        HeadingSource = calSource == "learned" ? "outline (learned)" : "outline (terrain fit)";
        return calSign * mb + calOffset;
    }

    public static double Wrap(double a) { a %= 2 * Math.PI; if (a > Math.PI) a -= 2 * Math.PI; if (a < -Math.PI) a += 2 * Math.PI; return a; }

    /// <summary>Projects the sun into a 1920x1080 frame for a camera looking along <paramref name="camHeading"/>.
    /// Returns false when the sun is outside the view (with a margin of <paramref name="margin"/> of the half-width).</summary>
    public static bool Project(double sunAz, double sunAlt, double camHeading, float hfovDeg, float pitchDeg, float margin, out SKPoint p, out double relAz)
    {
        p = default;
        relAz = Wrap(sunAz - camHeading);
        double hf = hfovDeg * Math.PI / 360, vf = Math.Atan(Math.Tan(hf) * 1080.0 / 1920.0);
        if (Math.Abs(relAz) >= Math.PI / 2) return false;
        // pinhole camera: the sun direction in camera space
        double x = Math.Sin(relAz) * Math.Cos(sunAlt), z = Math.Cos(relAz) * Math.Cos(sunAlt), y = Math.Sin(sunAlt);
        double pitch = pitchDeg * Math.PI / 180;                     // rotate about the camera's x axis
        double yc = y * Math.Cos(pitch) - z * Math.Sin(pitch), zc = y * Math.Sin(pitch) + z * Math.Cos(pitch);
        if (zc <= 0.05) return false;
        double u = x / zc / Math.Tan(hf), v = yc / zc / Math.Tan(vf);
        p = new SKPoint((float)(960 + u * 960), (float)(540 - v * 540));
        return Math.Abs(u) <= 1 + margin && Math.Abs(v) <= 1 + margin;
    }
}
