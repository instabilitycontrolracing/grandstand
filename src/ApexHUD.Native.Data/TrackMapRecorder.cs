using ApexHUD.Native.Common;
using IRSDKSharper;

namespace ApexHUD.Native.Data;

/// <summary>
/// Draws the track map from YOUR OWN driving (Angelo 2026-10-19), so every track has its
/// map long before the race intro needs it. Live telemetry has no GPS, so the lap is traced
/// by dead reckoning: the car's own velocity (VelocityX/Y, car frame) rotated by its heading
/// (Yaw) and integrated each 60 Hz tick. Verified against the GPS trace of a Le Mans .ibt:
/// 13.56 km lap, 2 m closure error, identical shape.
///
/// Rules: one clean, complete lap (start/finish to start/finish), on track the whole way,
/// never on pit road, no time jumps (pause / replay). The small closure error is spread
/// over the lap so the line meets itself. Once a track has a map the recorder stays idle there.
/// </summary>
sealed class TrackMapRecorder
{
    IRacingSdkDatum? vx, vy, yaw, pct, onTrack, onPit, sessTime;
    bool resolved;

    string track = "";
    bool trackDone;                     // map exists (or just saved) for the current track
    readonly List<(double X, double Y)> pts = new(8000);
    double x, y, lastT = -1;
    float lastPct = -1;
    bool collecting, valid;

    public void Tick(IRacingSdkData data, string? trackName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(trackName)) return;
            if (trackName != track)
            {
                track = trackName; trackDone = TrackMapStore.Has(track);
                Reset(false);
            }
            if (trackDone) return;
            if (!resolved)
            {
                var p = data.TelemetryDataProperties;
                p.TryGetValue("VelocityX", out vx); p.TryGetValue("VelocityY", out vy); p.TryGetValue("Yaw", out yaw);
                p.TryGetValue("LapDistPct", out pct); p.TryGetValue("IsOnTrack", out onTrack);
                p.TryGetValue("OnPitRoad", out onPit); p.TryGetValue("SessionTime", out sessTime);
                resolved = true;
                if (vx == null || vy == null || yaw == null || pct == null || sessTime == null) { trackDone = true; return; }
            }

            double t = data.GetDouble(sessTime!);
            float lp = data.GetFloat(pct!);
            bool ok = (onTrack == null || data.GetBool(onTrack)) && (onPit == null || !data.GetBool(onPit)) && lp >= 0;
            double dt = lastT < 0 ? 0 : t - lastT;
            lastT = t;
            if (!ok || dt < 0 || dt > 0.25) { Reset(false); lastPct = lp; return; }

            bool crossedLine = lastPct > 0.9f && lp < 0.1f;
            lastPct = lp;
            if (crossedLine)
            {
                if (collecting && valid) Finish();
                Reset(true);   // start a fresh lap right on the line
                return;
            }
            if (!collecting) return;

            double v1 = data.GetFloat(vx!), v2 = data.GetFloat(vy!), h = data.GetFloat(yaw!);
            double c = Math.Cos(h), s = Math.Sin(h);
            x += (v1 * c - v2 * s) * dt;
            y += (v1 * s + v2 * c) * dt;
            pts.Add((x, y));
        }
        catch { Reset(false); }
    }

    /// <summary>iRacing (re)connected: telemetry variables must be looked up again.</summary>
    public void Invalidate() { resolved = false; track = ""; Reset(false); lastT = -1; lastPct = -1; }

    void Reset(bool startLap)
    {
        pts.Clear(); x = 0; y = 0;
        collecting = startLap; valid = startLap;
        if (startLap) pts.Add((0, 0));
    }

    void Finish()
    {
        if (pts.Count < 600) return;
        double len = 0;
        for (int i = 1; i < pts.Count; i++) len += Math.Sqrt(Sq(pts[i].X - pts[i - 1].X) + Sq(pts[i].Y - pts[i - 1].Y));
        double ex = pts[^1].X - pts[0].X, ey = pts[^1].Y - pts[0].Y;
        if (len < 500 || Math.Sqrt(ex * ex + ey * ey) > len * 0.02) return;   // not a clean lap
        // spread the closure error over the lap so the line meets itself
        int n = pts.Count;
        var lap = new List<(double X, double Y)>(n);
        for (int i = 0; i < n; i++) { double f = i / (double)(n - 1); lap.Add((pts[i].X - ex * f, pts[i].Y - ey * f)); }
        string name = track;
        trackDone = true;
        ThreadPool.QueueUserWorkItem(_ => { try { TrackMapStore.Save(name, lap); } catch { } });
    }

    static double Sq(double v) => v * v;
}
