using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// The yellow-flag callout (2026-10-25, Angelo: "on the yellow flags we should have a graphic that plays ... the
/// lightboxes that do the flag colours, as an animation ... Yellow flag: (corner or straight name)", and "call out
/// the yellow flag, the corner or straight, and what it thinks happened").
///
/// When the car we follow gets a local yellow (SessionFlags yellow / yellow waving - not a full-course caution) a
/// glass panel drops in under the session bar: two LED marshal panels flashing yellow in turn, "YELLOW FLAG:" and the
/// place, and underneath what probably caused it. The cause is worked out from the cars themselves: the nearest car
/// ahead (within 2 km) that is off the track, stopped, or crawling (speed from its lap-distance movement). The place
/// is that car's corner or straight (CrewChief's track landmarks), else the one just ahead of us.
/// It stays while the yellow is out (at least 7 s, at most 14 s) and slides away.
///
/// 2026-10-25 (Angelo: "it can detect spins but it still cannot detect yellow flags"): replays don't carry iRacing's
/// local yellow for the car we watch, so Grandstand also calls the yellow itself, the way the marshals do: a car
/// stopped or crawling on the track, or stuck off it, for 1.5 s or more, within 2 km ahead of the car we follow. Speeds
/// run on the sim's own clock, so a paused or slow-motion replay doesn't look like a stopped field; and if a third of the
/// field looks stopped at once (formation, a red flag, a jump) it isn't treated as a yellow. A car parked for over
/// 90 s (retired, waiting for the tow) stops counting.
/// </summary>
public sealed class YellowFlagCallout
{
    public const float W = 660f, H = 104f;
    static readonly SKColor Yel = new(0xFF, 0xC1, 0x07), Glass = new(0x0B, 0x0E, 0x14, 232), Ivory = new(0xF4, 0xF6, 0xFA), Dim = new(0x2A, 0x24, 0x10);

    // per-car speed from lap distance (km/h), smoothed
    readonly Dictionary<int, (double T, float Pct, float Kmh)> motion = new();
    bool wasYellow;
    double shownAt = -1, yellowOffAt = -1;
    string place = "", cause = "";
    float alpha;
    double lastMs = -1;

    public bool IsActive => alpha > 0.01f || shownAt >= 0;
    /// <summary>Where the current yellow is (corner / straight), for the session bar's light box ("" if unknown).</summary>
    public static string CurrentPlace { get; private set; } = "";
    /// <summary>True while there's a yellow for the car we follow - iRacing's, or one Grandstand worked out (the session bar uses it).</summary>
    public static bool YellowNow { get; private set; }
    /// <summary>2026-10-27 (visual pack V8): lap fraction of the trouble (-1 unknown), the car in trouble (-1 none) and the
    /// cause line, as worked out when the yellow came out.</summary>
    public static float CurrentPct { get; private set; } = -1f;
    public static int CurrentCarIdx { get; private set; } = -1;
    public static string CurrentCause { get; private set; } = "";
    readonly Dictionary<int, double> troubleSince = new();
    readonly Dictionary<int, double> pitSeen = new();   // last time (sim clock) each car was on pit road

    public void Tick(RaceSnapshot snap, double nowMs)
    {
        double len = TrackCorners.LengthM(snap.Extras.TrackLength);
        // the sim's clock when we have it (replays at any speed, paused = no time passing), else the wall clock
        double t = snap.SessionTimeOfDay > 0 ? snap.SessionTimeOfDay : snap.CapturedAt.Subtract(DateTime.UnixEpoch).TotalSeconds;
        foreach (var cl in snap.ClassLeaderboard)
            foreach (var d in cl.Drivers)
            {
                if (d.LapDistPct < 0) continue;
                if (motion.TryGetValue(d.CarIdx, out var m) && t - m.T >= 0.25 && t - m.T < 3 && len > 0)
                {
                    float dp = d.LapDistPct - m.Pct; if (dp < -0.5f) dp += 1; if (dp > 0.5f) dp -= 1;
                    float kmh = (float)(Math.Max(0, dp) * len / (t - m.T) * 3.6);
                    motion[d.CarIdx] = (t, d.LapDistPct, m.Kmh < 0 ? kmh : m.Kmh * 0.5f + kmh * 0.5f);
                }
                else if (!motion.TryGetValue(d.CarIdx, out m) || t - m.T >= 3 || t < m.T) motion[d.CarIdx] = (t, d.LapDistPct, -1f);   // -1 = speed not known yet
            }

        // 2026-11-06 (#899/#924, Angelo: no flags at all outside a race - a YELLOW FLAG map card still came up in
        // qualifying): yellows only exist in a race session; this one switch silences the callout, the yellow map
        // card, the battle map's yellow and the session bar light together.
        bool raceSession = (snap.SessionTypeName ?? "").Contains("race", StringComparison.OrdinalIgnoreCase);
        bool racing = snap.IsConnected && snap.Phase == RacePhase.Broadcast && raceSession;
        bool own = racing && !snap.Flags.Caution && !snap.Flags.Checkered && !snap.Flags.Red && OwnYellow(snap, len, t);
        bool yellow = racing && ((snap.Flags.Yellow && !snap.Flags.Caution) || own);
        YellowNow = yellow;
        if (yellow && !wasYellow && shownAt < 0)
        {
            // 2026-10-28 (#485, Angelo: "keep the name of the turn or straight after the first yellow flag thing comes
            // on"): the first callout's corner / straight stays for that incident. A yellow that flickers off and back on
            // within 30 s, or that is re-read after we've passed the trouble (which reads "AHEAD"), keeps the name.
            bool sameIncident = yellowOffAt >= 0 && nowMs - yellowOffAt < 30000 && CurrentPlace.Length > 0;
            float oldPct = CurrentPct; int oldCar = CurrentCarIdx; string oldCause = CurrentCause;
            var (p2, c2) = Read(snap, len);
            if (sameIncident || (p2 == "AHEAD" && CurrentPlace.Length > 0 && yellowOffAt >= 0 && nowMs - yellowOffAt < 120000))
            {
                place = CurrentPlace;
                if (c2 == "INCIDENT AHEAD" && oldCause.Length > 0) { CurrentPct = oldPct; CurrentCarIdx = oldCar; CurrentCause = cause = oldCause; }
                else cause = c2;
            }
            else (place, cause) = (p2, c2);
            CurrentPlace = place == "AHEAD" ? "" : place;
            shownAt = nowMs; yellowOffAt = -1;
            StorySound.PlayBroadcast("yellow_flag");
            OverlayLog.Write($"yellow flag: {place} - {cause}");
            HighlightLog.Add("yellow", place == "AHEAD" ? "Yellow flag" : "Yellow flag: " + place, CurrentCarIdx >= 0 ? new[] { CurrentCarIdx } : Array.Empty<int>());   // #487
        }
        if (!yellow && wasYellow) yellowOffAt = nowMs;
        wasYellow = yellow;
        if (shownAt >= 0)
        {
            double up = nowMs - shownAt;
            bool done = up > 14000 || (up > 7000 && !yellow);
            if (done) shownAt = -1;
        }
    }

    /// <summary>A car in trouble on the road ahead of the car we follow (stopped / crawling on track, or stuck off it).</summary>
    bool OwnYellow(RaceSnapshot snap, double len, double t)
    {
        if (len <= 0) return false;
        var me = snap.Player;
        float mp = me?.LapDistPct ?? -1;
        int field = 0, troubled = 0; bool hit = false;
        foreach (var cl in snap.ClassLeaderboard)
            foreach (var d in cl.Drivers)
            {
                if (d.LapDistPct < 0 || d.IsPaceCar || d.IsSpectator || !d.IsConnected) { troubleSince.Remove(d.CarIdx); continue; }
                field++;
                if (d.IsInPit) pitSeen[d.CarIdx] = t;
                // the pit entry / exit lanes read as off track and slow: a car on pit road in the last 25 s isn't trouble
                bool nearPits = pitSeen.TryGetValue(d.CarIdx, out var ps) && t - ps >= 0 && t - ps < 25;
                if (d.IsInPit || nearPits || !motion.TryGetValue(d.CarIdx, out var m) || m.Kmh < 0) { troubleSince.Remove(d.CarIdx); continue; }
                bool bad = m.Kmh < 25 || (d.OffTrack && m.Kmh < 40);
                if (!bad) { troubleSince.Remove(d.CarIdx); continue; }
                troubled++;
                if (!troubleSince.TryGetValue(d.CarIdx, out var since) || t < since) troubleSince[d.CarIdx] = since = t;
                double held = t - since;
                if (held < 1.5 || held > 90) continue;
                if (me != null && d.CarIdx == me.CarIdx) continue;
                float ahead = mp >= 0 ? d.LapDistPct - mp : 0; if (ahead < 0) ahead += 1;
                if (mp < 0 || ahead * len <= 2000) hit = true;
            }
        if (field > 0 && troubled > field / 3) return false;               // the whole field slow: not a yellow
        return hit;
    }

    (string Place, string Cause) Read(RaceSnapshot snap, double len)
    {
        var me = snap.Player;
        float mp = me?.LapDistPct ?? -1;
        Driver? best = null; float bestAhead = float.MaxValue; string why = "";
        foreach (var cl in snap.ClassLeaderboard)
            foreach (var d in cl.Drivers)
            {
                if (d.LapDistPct < 0 || d.IsInPit || d.IsPaceCar || (me != null && d.CarIdx == me.CarIdx)) continue;
                float kmh = motion.TryGetValue(d.CarIdx, out var m) && m.Kmh >= 0 ? m.Kmh : 999;
                string w = d.OffTrack ? "OFF TRACK" : kmh < 15 ? "STOPPED ON TRACK" : kmh < 70 ? "SLOW ON TRACK" : "";
                if (w.Length == 0) continue;
                float ahead = mp >= 0 ? d.LapDistPct - mp : 0; if (ahead < 0) ahead += 1;
                if (len > 0 && ahead * len > 2000) continue;               // the yellow is for trouble just up the road
                if (ahead < bestAhead) { bestAhead = ahead; best = d; why = w; }
            }
        float at = best?.LapDistPct ?? (mp >= 0 ? mp + (len > 0 ? (float)(300 / len) : 0.02f) : -1);
        string pl = "";
        if (at >= 0)
        {
            at -= MathF.Floor(at);
            var cn = TrackCorners.Named(snap.TrackRawName, snap.Extras.TrackLength, at, snap.TrackBroadcastName);
            var st = cn == null ? TrackCorners.Straight(snap.TrackRawName, snap.Extras.TrackLength, at) : null;
            pl = cn ?? (st != null ? Cap(st.StartsWith("the ") ? st[4..] : st) : "");
        }
        string cs = best != null ? $"CAR #{best.CarNumber} {why}" : "INCIDENT AHEAD";
        CurrentPct = at; CurrentCarIdx = best?.CarIdx ?? -1; CurrentCause = cs;
        return (pl.Length > 0 ? pl.ToUpperInvariant() : "AHEAD", cs);
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public void Draw(SKCanvas c, float cx, float top, double nowMs)
    {
        double dt = lastMs < 0 ? 16 : Math.Clamp(nowMs - lastMs, 0, 100); lastMs = nowMs;
        alpha = Math.Clamp(alpha + (float)(dt / 260.0) * (shownAt >= 0 ? 1 : -1), 0f, 1f);
        if (alpha <= 0.01f) return;
        float a = alpha * alpha * (3 - 2 * alpha);
        byte A(float v) => (byte)Math.Clamp(v * 255f * a, 0, 255);
        float y = top - (1 - a) * 26f;
        var r = new SKRect(cx - W / 2, y, cx + W / 2, y + H);

        using var fill = new SKPaint { IsAntialias = true };
        fill.Color = new SKColor(0, 0, 0, A(0.35f)); c.DrawRect(new SKRect(r.Left + 6, r.Top + 6, r.Right + 6, r.Bottom + 6), fill);
        fill.Color = Glass.WithAlpha(A(Glass.Alpha / 255f)); c.DrawRect(r, fill);
        Theme.Skin(c, r, ThemeSurface.Popup, nowMs, a);   // #601: the broadcast theme (nothing on Default)
        fill.Color = Yel.WithAlpha(A(1)); c.DrawRect(new SKRect(r.Left, r.Top, r.Right, r.Top + 4), fill);

        // two LED marshal panels, flashing in turn (like the light boards at real circuits)
        double ph = nowMs / 1000.0 * 2.2;
        for (int k = 0; k < 2; k++)
        {
            bool on = ((int)Math.Floor(ph) + k) % 2 == 0;
            var p = new SKRect(r.Left + 16 + k * 62, r.Top + 18, r.Left + 16 + k * 62 + 54, r.Top + 18 + 72);
            fill.Color = new SKColor(0x05, 0x06, 0x08, A(1)); c.DrawRoundRect(p, 6, 6, fill);
            const int n = 6;
            float cell = (p.Width - 10) / n, rad = cell * 0.34f;
            int rows = (int)((p.Height - 10) / cell);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < rows; j++)
                {
                    float x = p.Left + 5 + cell * (i + 0.5f), yy = p.Top + 5 + cell * (j + 0.5f);
                    if (on)
                    {
                        fill.Color = Yel.WithAlpha(A(0.28f)); c.DrawCircle(x, yy, rad * 1.9f, fill);
                        fill.Color = Yel.WithAlpha(A(1)); c.DrawCircle(x, yy, rad, fill);
                    }
                    else { fill.Color = Dim.WithAlpha(A(1)); c.DrawCircle(x, yy, rad, fill); }
                }
        }

        // YELLOW FLAG: PORSCHE CURVES  /  CAR #23 STOPPED ON TRACK
        float tx = r.Left + 16 + 2 * 62 + 14, maxW = r.Right - 18 - tx;
        using var head = new SKPaint { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Black, TextSize = 40, Color = Yel.WithAlpha(A(1)) };
        using var pl = new SKPaint { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Black, TextSize = 40, Color = Ivory.WithAlpha(A(1)) };
        string h1 = "YELLOW FLAG: ";
        float hw = head.MeasureText(h1);
        while (hw + pl.MeasureText(place) > maxW && pl.TextSize > 22) { pl.TextSize -= 1; head.TextSize = pl.TextSize; hw = head.MeasureText(h1); }
        c.DrawText(h1, tx, r.Top + 56, head);
        c.DrawText(place, tx + hw, r.Top + 56, pl);
        using var sub = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 15, Color = new SKColor(0xDE, 0xE2, 0xE8, A(1)) };   // #340: was C9CED6
        TextTracked.Draw(c, sub, cause, tx, r.Top + 84, 2.2f);
    }
}
