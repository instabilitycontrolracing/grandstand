using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApexHUD.Native.Common;

/// <summary>One stop of the session review: a named corner or a straight, with what the driver did there.
/// Speeds in km/h, distances in metres, times in seconds. Null = not measured (never guessed).</summary>
public sealed record ReviewSegment
{
    public string Name { get; init; } = "";
    public bool Straight { get; init; }
    /// <summary>Where it lies on the lap (fractions 0..1; End &lt; Start when it crosses the line).</summary>
    public float StartPct { get; init; }
    public float EndPct { get; init; }
    public double LengthM { get; init; }
    /// <summary>Clean passes measured (no pit lane, caution, off, spin or incident in it; not the race's first lap).</summary>
    public int Passes { get; init; }
    /// <summary>Every clean pass's time, in lap order.</summary>
    public float[]? PassTimes { get; init; }
    public double? BestTime { get; init; }
    public int BestLap { get; init; }
    public double? TypicalTime { get; init; }
    /// <summary>Standard deviation of the clean passes, seconds.</summary>
    public double? Spread { get; init; }
    /// <summary>Typical minus best: what matching your own best here is worth, every lap.</summary>
    public double? Gain => BestTime is { } b && TypicalTime is { } t ? Math.Max(0, t - b) : null;
    public float? MinKmhBest { get; init; }
    public float? MinKmhTypical { get; init; }
    public float? TopKmhBest { get; init; }
    public float? TopKmhTypical { get; init; }
    public float? EntryKmhBest { get; init; }
    public float? EntryKmhTypical { get; init; }
    public float? ExitKmhBest { get; init; }
    public float? ExitKmhTypical { get; init; }
    /// <summary>Corners: metres from the brake point to the slowest point (null = no braking: flat out).</summary>
    public float? BrakeToApexBest { get; init; }
    public float? BrakeToApexTypical { get; init; }
    /// <summary>Metres the best pass braked later than the typical one (negative = earlier).</summary>
    public float? BrakeLaterBy { get; init; }
    /// <summary>Corners: metres from the slowest point back to full throttle.</summary>
    public float? ThrottleAfterApexBest { get; init; }
    public float? ThrottleAfterApexTypical { get; init; }
    /// <summary>Metres sooner the best pass was back to full throttle (by lap position).</summary>
    public float? ThrottleSoonerBy { get; init; }
    public int? Gear { get; init; }
    /// <summary>Best pass, as lap fractions: brake point, slowest point, full throttle (null when absent).</summary>
    public float? BrakePct { get; init; }
    public float? ApexPct { get; init; }
    public float? ThrottlePct { get; init; }
    public int Incidents { get; init; }
    public int Offs { get; init; }
    public int Spins { get; init; }
    /// <summary>Speed trace (km/h) at <see cref="TracePoints"/> evenly spaced points from TraceFromPct over TraceSpanPct:
    /// the best pass, the typical (median) pass, and the best pass's brake / throttle (0..1).</summary>
    public float TraceFromPct { get; init; }
    public float TraceSpanPct { get; init; }
    public float[]? TraceBest { get; init; }
    public float[]? TraceTypical { get; init; }
    public float[]? BrakeBest { get; init; }
    public float[]? ThrottleBest { get; init; }
    public const int TracePoints = 72;
}

public sealed record ReviewLap(int Number, double Time, bool Pit, bool Clean, int Incidents, bool Counted = true);

/// <summary>2026-10-27 — the breakdown of the most recent session, read from iRacing's .ibt telemetry (Session Review).</summary>
public sealed record SessionReviewData
{
    public const int CurrentVersion = 4;
    public int Version { get; init; } = CurrentVersion;
    public string SourceFile { get; init; } = "";
    public long SourceSize { get; init; }
    public DateTime SourceWriteUtc { get; init; }
    public DateTime SessionStart { get; init; }
    public string TrackRaw { get; init; } = "";
    public string TrackDisplay { get; init; } = "";
    public string TrackConfig { get; init; } = "";
    public string TrackCity { get; init; } = "";
    public string TrackCountry { get; init; } = "";
    public string TrackLength { get; init; } = "";
    public double TrackLat { get; init; } = double.NaN;
    public double TrackLon { get; init; } = double.NaN;
    public int TrackTurns { get; init; }
    public string Driver { get; init; } = "";
    public string Car { get; init; } = "";
    public string CarClass { get; init; } = "";
    /// <summary>iRacing's SessionType ("Race", "Practice", "Lone Qualify", ...).</summary>
    public string SessionType { get; init; } = "";
    public bool IsRace { get; init; }
    public int LapsCompleted { get; init; }
    public int CleanLaps { get; init; }
    public double? BestLap { get; init; }
    public int BestLapNo { get; init; }
    public double? TypicalLap { get; init; }
    public double? Theoretical { get; init; }
    public int? StartClassPos { get; init; }
    public int? FinishClassPos { get; init; }
    public int? FinishPos { get; init; }
    public int? ClassSize { get; init; }
    public int Incidents { get; init; }
    public int Offs { get; init; }
    public int Spins { get; init; }
    public int PitStops { get; init; }
    public float? TopKmh { get; init; }
    public float? AirC { get; init; }
    public float? TrackC { get; init; }
    public double DrivenMinutes { get; init; }
    public List<ReviewLap> Laps { get; init; } = new();
    public List<ReviewSegment> Segments { get; init; } = new();
    /// <summary>Indices into <see cref="Segments"/> of the places with the most to gain, biggest first (up to 3).</summary>
    public List<int> Focus { get; init; } = new();
    /// <summary>True when the corners came from the track's named landmarks; false = found from the speed trace.</summary>
    public bool NamedCorners { get; init; }
    /// <summary>The circuit from the fastest lap's GPS (x east, y south, metres; from the line in driving order),
    /// for tracks Grandstand has no recorded map of. Null when the file has no position channels.</summary>
    public float[]? Outline { get; init; }

    public static readonly JsonSerializerOptions Json = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// 2026-10-27 — turns one .ibt into a <see cref="SessionReviewData"/>. Picks the last session in the file with a
/// completed lap, splits the lap into the track's named corners and the straights between them (a gapless partition,
/// so the best pieces add up to a theoretical lap), and measures every clean pass through each piece.
/// Everything shown comes from the file; anything that can't be measured is left null.
/// </summary>
public static class SessionReviewAnalyzer
{
    static readonly string[] Channels =
    {
        "SessionTime", "SessionNum", "SessionState", "SessionFlags", "Lap", "LapDistPct", "Speed", "Throttle", "Brake", "Gear",
        "LapLastLapTime", "PlayerCarMyIncidentCount", "PlayerCarPosition", "PlayerCarClassPosition", "PlayerTrackSurface",
        "OnPitRoad", "IsOnTrack", "VelocityX", "VelocityY", "AirTemp", "TrackTempCrew", "Lat", "Lon",
    };

    /// <summary>Reads and analyses the file. Returns null when it has no completed lap (a stub).</summary>
    public static SessionReviewData? Analyze(string path, CancellationToken ct = default)
    {
        var ibt = IbtFile.Open(path);
        if (ibt.RecordCount < ibt.TickRate * 30) return null;
        var ch = ibt.Read(Channels, ct);
        double[] Get(string n) => ch.TryGetValue(n, out var a) ? a : new double[ch["SessionTime"].Length];
        var fi = new FileInfo(path);
        return Analyze(ibt, Get, fi.Length, fi.LastWriteTimeUtc, ct);
    }

    sealed class Lap
    {
        public int Number;
        public int Row0, Row1;
        public double T0, T1, Time;
        public bool Pit, Broken, Caution, Start, Invalid;
        public int Incidents, Offs, Spins;
        public bool Clean => !Pit && !Broken && !Caution && !Start && !Invalid && Incidents == 0 && Offs == 0 && Spins == 0;
        public bool Timed => !Pit && !Broken && !Start;
        /// <summary>Counts for the best lap: timed, and iRacing didn't throw the lap time out (track limits).</summary>
        public bool Counts => Timed && !Invalid;
    }

    record struct Event(double U, double T, int Kind, int Points);   // kind 0 off, 1 spin, 2 incident

    static SessionReviewData? Analyze(IbtFile ibt, Func<string, double[]> Get, long size, DateTime writeUtc, CancellationToken ct)
    {
        string y = ibt.SessionInfo;
        var time = Get("SessionTime"); var sess = Get("SessionNum"); var state = Get("SessionState"); var flags = Get("SessionFlags");
        var lapCh = Get("Lap"); var pct = Get("LapDistPct"); var spd = Get("Speed"); var thr = Get("Throttle"); var brk = Get("Brake");
        var gear = Get("Gear"); var lastLap = Get("LapLastLapTime"); var inc = Get("PlayerCarMyIncidentCount");
        var pos = Get("PlayerCarPosition"); var cpos = Get("PlayerCarClassPosition"); var surf = Get("PlayerTrackSurface");
        var pit = Get("OnPitRoad"); var onTrack = Get("IsOnTrack"); var vx = Get("VelocityX"); var vy = Get("VelocityY");
        var air = Get("AirTemp"); var trk = Get("TrackTempCrew");
        int N = time.Length;
        if (N < 100) return null;

        string trackRaw = IbtFile.Yaml(y, "WeekendInfo", "TrackName");
        string trackLen = IbtFile.Yaml(y, "WeekendInfo", "TrackLength");
        double lenM = TrackCorners.LengthM(trackLen);
        if (lenM <= 0) return null;

        // ---- which session: the last one with a completed lap ----
        var sessions = new List<(int Num, int R0, int R1)>();
        for (int i = 0; i < N; i++)
        {
            int s = (int)sess[i];
            if (sessions.Count == 0 || sessions[^1].Num != s) sessions.Add((s, i, i));
            else sessions[^1] = (s, sessions[^1].R0, i);
        }
        List<Lap>? laps = null; (int Num, int R0, int R1) pick = default;
        double[] u = new double[N];
        for (int si = sessions.Count - 1; si >= 0; si--)
        {
            var cand = BuildLaps(sessions[si].R0, sessions[si].R1);
            if (cand.Count > 0) { laps = cand; pick = sessions[si]; break; }
        }
        if (laps == null) return null;
        int r0 = pick.R0, r1 = pick.R1;
        var sInfo = IbtFile.YamlList(y, "SessionInfo", "Sessions").FirstOrDefault(d => d.TryGetValue("SessionNum", out var n) && n == pick.Num.ToString(CultureInfo.InvariantCulture));
        string sType = sInfo != null && sInfo.TryGetValue("SessionType", out var st) ? st : "";
        bool isRace = sType.Contains("Race", StringComparison.OrdinalIgnoreCase);

        // ---- continuous lap position u (laps + fraction) for the chosen session, and where it jumps (tows / resets) ----
        List<Lap> BuildLaps(int a, int b)
        {
            var broken = new bool[N];
            double prevP = -1;
            for (int i = a; i <= b; i++)
            {
                double p = pct[i];
                bool ok = p >= 0 && p <= 1.0001 && onTrack[i] > 0.5;
                if (!ok) { u[i] = i > a ? u[i - 1] : 0; broken[i] = true; prevP = -1; continue; }
                if (prevP < 0) { u[i] = i > a && !double.IsNaN(u[i - 1]) ? Math.Floor(u[i - 1]) + p + (p < (u[i - 1] - Math.Floor(u[i - 1])) - 0.5 ? 1 : 0) : (lapCh[i] + p); if (i > a) broken[i] = true; }
                else
                {
                    double d = p - prevP;
                    if (d < -0.5) d += 1; else if (d > 0.5) d -= 1;
                    if (Math.Abs(d) > 0.02) broken[i] = true;
                    u[i] = u[i - 1] + d;
                }
                prevP = p;
            }
            // lap starts: u crossing an integer going forward, numbered by the Lap channel just after
            var outL = new List<Lap>();
            Lap? cur = null;
            for (int i = a + 1; i <= b; i++)
            {
                if (broken[i]) { if (cur != null) cur.Broken = true; continue; }
                if (Math.Floor(u[i]) > Math.Floor(u[i - 1]) && u[i] > u[i - 1])
                {
                    double k = Math.Floor(u[i]);
                    double tc = time[i - 1] + (time[i] - time[i - 1]) * (k - u[i - 1]) / (u[i] - u[i - 1]);
                    if (cur != null) { cur.T1 = tc; cur.Row1 = i; cur.Time = tc - cur.T0; outL.Add(cur); }
                    cur = new Lap { Number = (int)lapCh[i], T0 = tc, Row0 = i };
                }
                if (cur != null)
                {
                    if (pit[i] > 0.5) cur.Pit = true;
                    if (((long)flags[i] & 0xC000) != 0) cur.Caution = true;
                }
            }
            outL = outL.Where(l => l.Number > 0).ToList();
            for (int i = 1; i < outL.Count; i++) outL[i].Number = outL[0].Number + i;   // pit-lane crossings confuse the Lap channel
            return outL;
        }

        // monotonic envelope of u for lookups
        var um = new double[N];
        for (int i = r0; i <= r1; i++) um[i] = i == r0 ? u[i] : Math.Max(um[i - 1], u[i]);
        int RowAt(double uu)
        {
            int lo = r0, hi = r1;
            if (um[hi] < uu) return -1;
            while (lo < hi) { int m = (lo + hi) / 2; if (um[m] < uu) lo = m + 1; else hi = m; }
            return lo;
        }
        double TimeAt(double uu)
        {
            int i = RowAt(uu);
            if (i <= r0) return i == r0 ? time[r0] : double.NaN;
            double du = u[i] - u[i - 1];
            return du > 1e-9 ? time[i - 1] + (time[i] - time[i - 1]) * Math.Clamp((uu - u[i - 1]) / du, 0, 1) : time[i];
        }
        double ValAt(double[] ch, double uu)
        {
            int i = RowAt(uu);
            if (i <= r0) return i == r0 ? ch[r0] : double.NaN;
            double du = u[i] - u[i - 1];
            double k = du > 1e-9 ? Math.Clamp((uu - u[i - 1]) / du, 0, 1) : 1;
            return ch[i - 1] + (ch[i] - ch[i - 1]) * k;
        }

        // ---- the start of a race: its first racing lap is never "typical" ----
        if (isRace && laps.Count > 0) laps[0].Start = true;
        foreach (var l in laps) if (l.Number == 1 && isRace) l.Start = true;

        // ---- events: offs, spins, incidents, by where they happened ----
        var events = new List<Event>();
        int offRun = 0, spinRun = 0;
        for (int i = r0 + 1; i <= r1; i++)
        {
            bool off = surf[i] == 0 && onTrack[i] > 0.5;
            offRun = off ? offRun + 1 : 0;
            if (offRun == (int)(0.3 * ibt.TickRate)) events.Add(new Event(u[i - offRun + 1], time[i - offRun + 1], 0, 0));
            double slip = Math.Abs(Math.Atan2(vy[i], vx[i])) * 180 / Math.PI;
            bool sp = spd[i] > 8 && slip > 50;
            spinRun = sp ? spinRun + 1 : (spinRun > 0 && spd[i] < 3 ? spinRun : 0);
            if (sp && spinRun == (int)(0.25 * ibt.TickRate)) events.Add(new Event(u[i - spinRun + 1], time[i - spinRun + 1], 1, 0));
            if (!sp && spd[i] >= 3) spinRun = 0;
            int dInc = (int)(inc[i] - inc[i - 1]);
            if (dInc > 0)
            {
                // placed where the trouble started: the off / spin just before it, else ~1 s earlier
                var near = events.LastOrDefault(e => e.Kind < 2 && time[i] - e.T < 5 && e.T <= time[i]);
                double eu = near.T > 0 ? near.U : ValAtTime(time[i] - 1.0);
                events.Add(new Event(eu, time[i], 2, dInc));
            }
        }
        double ValAtTime(double t)
        {
            int lo = r0, hi = r1;
            while (lo < hi) { int m = (lo + hi) / 2; if (time[m] < t) lo = m + 1; else hi = m; }
            return u[lo];
        }
        foreach (var l in laps)
            foreach (var e in events)
                if (e.T >= l.T0 && e.T < l.T1)
                {
                    if (e.Kind == 0) l.Offs++; else if (e.Kind == 1) l.Spins++; else l.Incidents += e.Points;
                }

        // iRacing's own lap time when it matches ours (it's what the driver saw)
        foreach (var l in laps)
        {
            int j = Math.Min(r1, l.Row1 + (int)(2.0 * ibt.TickRate));
            double ir = lastLap[j];
            if (ir > 0 && Math.Abs(ir - l.Time) < 0.08) l.Time = ir;
            else if (ir < 0 && !l.Pit) l.Invalid = true;            // iRacing didn't count this lap
        }

        // ---- the pieces of the lap ----
        bool named;
        var pieces = Pieces(trackRaw, trackLen, lenM, out named);
        if (pieces.Count == 0)
        {
            named = false;
            var fast = laps.Where(l => l.Timed).OrderBy(l => l.Time).FirstOrDefault() ?? laps[0];
            double kf = Math.Round(u[fast.Row0]);
            pieces = PiecesFromSpeed(f => ValAt(spd, kf + f) * 3.6, lenM);
        }

        // ---- every pass through every piece ----
        var segs = new List<ReviewSegment>();
        // laps iRacing threw out (track limits) never set a "best" piece: a cut would flatter the theoretical lap
        var timedLaps = laps.Where(l => l.Counts).ToList();
        foreach (var pc in pieces)
        {
            ct.ThrowIfCancellationRequested();
            double s0 = pc.A, s1 = pc.B; if (s1 <= s0) s1 += 1;
            double segLen = (s1 - s0) * lenM;
            var passes = new List<(Lap L, double T, double U0, double U1)>();
            foreach (var l in timedLaps)
            {
                double k = Math.Round(u[l.Row0]);           // the integer this lap started at
                double u0 = k + s0, u1 = k + s1;
                if (!Clean(u0, u1, l)) continue;
                double t0 = TimeAt(u0), t1 = TimeAt(u1);
                if (double.IsNaN(t0) || double.IsNaN(t1) || t1 <= t0) continue;
                passes.Add((l, t1 - t0, u0, u1));
            }
            int incN = 0, offN = 0, spinN = 0;
            foreach (var e in events)
            {
                double f = e.U - Math.Floor(e.U);
                bool inside = pc.B > pc.A ? f >= pc.A && f < pc.B : f >= pc.A || f < pc.B;
                if (!inside) continue;
                if (e.Kind == 0) offN++; else if (e.Kind == 1) spinN++; else incN += e.Points;
            }
            if (passes.Count == 0)
            {
                segs.Add(new ReviewSegment { Name = pc.Name, Straight = pc.Straight, StartPct = (float)pc.A, EndPct = (float)pc.B, LengthM = segLen,
                                             Incidents = incN, Offs = offN, Spins = spinN });
                continue;
            }
            var best = passes.OrderBy(p => p.T).First();
            var times = passes.Select(p => p.T).OrderBy(v => v).ToList();
            double typical = Median(times);
            double? spread = times.Count >= 3 ? StdDev(times) : null;

            // per-pass measurements
            // braking for a corner that follows another corner must start inside it (else it belongs to the one before)
            int pIdx = pieces.IndexOf(pc);
            double backM = pieces[(pIdx - 1 + pieces.Count) % pieces.Count].Straight ? 300 : 40;
            var m = passes.Select(p => Measure(p.U0, p.U1, pc.Straight, backM)).ToList();
            var mb = Measure(best.U0, best.U1, pc.Straight, backM);
            float? MedF(Func<PassM, double?> f) { var v = m.Select(f).Where(x => x.HasValue).Select(x => x!.Value).OrderBy(x => x).ToList(); return v.Count == 0 ? null : (float)Median(v); }
            // a corner taken flat on most passes shows no brake point
            int braked = m.Count(x => x.BrakeU.HasValue);
            bool brakes = braked * 2 > m.Count;
            bool apex = !pc.Straight && !mb.Edge && m.Count(x => x.Edge) * 2 <= m.Count;
            if (!apex) brakes = false;

            // traces
            double pad = Math.Max(60.0 / lenM, (s1 - s0) * 0.18);
            double tf = s0 - pad, tspan = (s1 - s0) + 2 * pad;
            int P = ReviewSegment.TracePoints;
            float[] Trace(double[] chn, double u0base, double scale) { var a = new float[P]; for (int i = 0; i < P; i++) a[i] = (float)(ValAt(chn, u0base + tf + tspan * i / (P - 1)) * scale); return a; }
            double kb = best.U0 - s0;
            var trBest = Trace(spd, kb, 3.6);
            var trTyp = new float[P];
            for (int i = 0; i < P; i++)
            {
                var v = passes.Select(p => ValAt(spd, (p.U0 - s0) + tf + tspan * i / (P - 1)) * 3.6).Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList();
                trTyp[i] = v.Count > 0 ? (float)Median(v) : float.NaN;
            }

            float? brakeLater = null, thrSooner = null;
            {
                // compare by position on the lap: each pass's lap base is the integer its window starts from
                var bOthers = new List<double>(); var tOthers = new List<double>();
                for (int i = 0; i < passes.Count; i++)
                {
                    double kp = Math.Round(passes[i].U0 - s0);
                    if (m[i].BrakeU is { } bu) bOthers.Add(bu - kp);
                    if (m[i].ThrottleU is { } tu) tOthers.Add(tu - kp);
                }
                bOthers.Sort(); tOthers.Sort();
                double kbest = Math.Round(best.U0 - s0);
                if (brakes && mb.BrakeU is { } bub && bOthers.Count > 0) brakeLater = (float)((bub - kbest - Median(bOthers)) * lenM);
                if (mb.ThrottleU is { } tub && tOthers.Count > 0) thrSooner = (float)((Median(tOthers) - (tub - kbest)) * lenM);
            }
            int? gearMode = m.Where(x => x.Gear > 0).GroupBy(x => x.Gear).OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault();
            float Frac(double v) => (float)(v - Math.Floor(v));

            var seg = new ReviewSegment
            {
                Name = pc.Name, Straight = pc.Straight, StartPct = (float)pc.A, EndPct = (float)pc.B, LengthM = segLen,
                Passes = passes.Count, PassTimes = passes.Select(p => (float)p.T).ToArray(), BestTime = best.T, BestLap = best.L.Number, TypicalTime = typical, Spread = spread,
                MinKmhBest = apex ? (float?)mb.MinKmh : null, MinKmhTypical = apex ? MedF(x => x.MinKmh) : null,
                ExitKmhBest = (float)mb.ExitKmh, ExitKmhTypical = MedF(x => x.ExitKmh),
                TopKmhBest = (float)mb.TopKmh, TopKmhTypical = MedF(x => x.TopKmh),
                EntryKmhBest = (float)mb.EntryKmh, EntryKmhTypical = MedF(x => x.EntryKmh),
                BrakeToApexBest = brakes && !pc.Straight ? (float?)mb.BrakeToApex : null,
                BrakeToApexTypical = brakes && !pc.Straight ? MedF(x => x.BrakeToApex) : null,
                BrakeLaterBy = pc.Straight ? null : brakeLater,
                ThrottleAfterApexBest = apex ? (float?)mb.ThrottleAfterApex : null,
                ThrottleAfterApexTypical = apex ? MedF(x => x.ThrottleAfterApex) : null,
                ThrottleSoonerBy = apex ? thrSooner : null,
                Gear = gearMode,
                BrakePct = brakes && !pc.Straight && mb.BrakeU is { } b1 ? Frac(b1) : null,
                ApexPct = apex ? Frac(mb.ApexU) : null,
                ThrottlePct = apex && mb.ThrottleU is { } t1u ? Frac(t1u) : null,
                Incidents = incN, Offs = offN, Spins = spinN,
                TraceFromPct = (float)(tf - Math.Floor(tf)), TraceSpanPct = (float)tspan,
                TraceBest = trBest, TraceTypical = trTyp,
                BrakeBest = Trace(brk, kb, 1), ThrottleBest = Trace(thr, kb, 1),
            };
            segs.Add(seg);
        }

        bool Clean(double u0, double u1, Lap l)
        {
            int a = RowAt(u0 - 0.002), b = RowAt(u1 + 0.002);
            if (a < 0 || b < 0) return false;
            for (int i = Math.Max(r0 + 1, a); i <= b; i++)
            {
                if (pit[i] > 0.5 || ((long)flags[i] & 0xC000) != 0) return false;
                if (u[i] < u[i - 1] - 1e-6) return false;           // went backwards
                double p = pct[i], q = pct[i - 1];
                double d = p - q; if (d < -0.5) d += 1; else if (d > 0.5) d -= 1;
                if (Math.Abs(d) > 0.02 || onTrack[i] < 0.5) return false;
            }
            foreach (var e in events)
                if (e.U >= u0 - 0.002 && e.U <= u1 + 0.002) return false;
            return true;
        }

        PassM Measure(double u0, double u1, bool straight, double backM)
        {
            int a = RowAt(u0), b = RowAt(u1);
            var pm = new PassM();
            if (a < 0 || b < 0 || b <= a) return pm;
            double mn = double.MaxValue, mx = 0; int mi = a;
            for (int i = a; i <= b; i++) { if (spd[i] < mn) { mn = spd[i]; mi = i; } if (spd[i] > mx) mx = spd[i]; }
            pm.MinKmh = mn * 3.6; pm.TopKmh = mx * 3.6; pm.EntryKmh = ValAt(spd, u0) * 3.6;
            pm.ApexU = u[mi]; pm.Gear = (int)gear[mi]; pm.ExitKmh = ValAt(spd, u1) * 3.6;
            if (straight) return pm;
            // still braking as it leaves: the slowest point is in the next piece (a chicane split in two) - no apex here
            if ((u1 - u[mi]) * lenM < 20 && brk[mi] > 0.1) { pm.Edge = true; return pm; }
            // brake point: back from the slowest point to the braking zone (Brake > 30 %), then to where it began (> 5 %)
            // (never further back than backM before the corner: a zone that started earlier belongs to the corner before)
            int lim = RowAt(u0 - backM / lenM); if (lim < r0) lim = r0;
            int j = mi;
            while (j > lim && brk[j] < 0.3) j--;
            if (j > lim && brk[j] >= 0.3)
            {
                while (j > lim && brk[j - 1] > 0.1) j--;
                if (j > lim) { pm.BrakeU = u[j]; pm.BrakeToApex = (u[mi] - u[j]) * lenM; }
            }
            int lim2 = RowAt(Math.Min(u1 + 400 / lenM, um[r1])); if (lim2 < 0) lim2 = r1;
            for (int i = mi; i <= lim2; i++)
                if (thr[i] >= 0.95) { pm.ThrottleU = u[i]; pm.ThrottleAfterApex = (u[i] - u[mi]) * lenM; break; }
            return pm;
        }

        // ---- headline ----
        var timed = laps.Where(l => l.Counts && l.Time > 0).ToList();
        var bestLap = timed.OrderBy(l => l.Time).FirstOrDefault();
        var cleanTimes = laps.Where(l => l.Clean).Select(l => l.Time).OrderBy(v => v).ToList();
        double? theo = segs.Count > 0 && segs.All(s => s.BestTime.HasValue) ? segs.Sum(s => s.BestTime!.Value) : null;
        if (theo is { } th && bestLap != null && th > bestLap.Time + 0.0005) theo = null;   // pieces didn't line up: say nothing

        int playerIdx = int.TryParse(IbtFile.Yaml(y, "DriverInfo", "DriverCarIdx"), out var pi) ? pi : -1;
        var drivers = IbtFile.YamlList(y, "DriverInfo", "Drivers");
        var me = drivers.FirstOrDefault(d => d.TryGetValue("CarIdx", out var ci) && ci == playerIdx.ToString(CultureInfo.InvariantCulture));
        string cls = me != null && me.TryGetValue("CarClassShortName", out var cs) ? cs : "";
        int? classSize = null;
        if (cls.Length > 0)
            classSize = drivers.Count(d => d.TryGetValue("CarClassShortName", out var c2) && c2 == cls
                                       && !(d.TryGetValue("IsSpectator", out var sp) && sp == "1") && !(d.TryGetValue("CarIsPaceCar", out var pc) && pc == "1"));

        int? startPos = null, finishPos = null, finishAll = null;
        if (isRace)
        {
            for (int i = r0; i <= r1; i++) if (state[i] == 4 && cpos[i] > 0) { startPos = (int)cpos[i]; break; }
            int fin = -1;
            for (int i = r0; i <= r1; i++) if (state[i] >= 5) { fin = i; break; }
            if (fin < 0) fin = r1;
            for (int i = fin; i >= r0; i--) if (cpos[i] > 0) { finishPos = (int)cpos[i]; finishAll = pos[i] > 0 ? (int)pos[i] : null; break; }
        }
        int pitStops = 0;
        for (int i = r0 + 1; i <= r1; i++) if (pit[i] > 0.5 && pit[i - 1] < 0.5 && state[i] >= 3) pitStops++;
        if (isRace && pitStops > 0 && pit[r1] > 0.5) pitStops--;   // the drive back in after the flag isn't a stop
        double top = 0; for (int i = r0; i <= r1; i++) if (spd[i] > top && pit[i] < 0.5) top = spd[i];
        float? Avg(double[] a) { double s = 0; int n = 0; for (int i = r0; i <= r1; i += 60) if (a[i] != 0 && !double.IsNaN(a[i])) { s += a[i]; n++; } return n > 0 ? (float)(s / n) : null; }

        // focus: the three places with the most time to find (each named once)
        var focus = segs.Select((s, i) => (s, i)).Where(x => x.s.Gain is > 0.02).OrderByDescending(x => x.s.Gain).Take(3).Select(x => x.i).ToList();

        int.TryParse(IbtFile.Yaml(y, "WeekendInfo", "TrackNumTurns"), out int turns);

        // the outline from GPS (fastest lap)
        float[]? outline = null;
        var lat = Get("Lat"); var lon = Get("Lon");
        var ol = timed.OrderBy(l => l.Time).FirstOrDefault() ?? laps.FirstOrDefault(l => !l.Broken);
        if (ol != null && lat[ol.Row0] != 0)
        {
            double kb = Math.Round(u[ol.Row0]), la0 = lat[ol.Row0], lo0 = lon[ol.Row0], cosL = Math.Cos(la0 * Math.PI / 180);
            const int OP = 720;
            outline = new float[OP * 2];
            for (int i = 0; i < OP; i++)
            {
                double la = ValAt(lat, kb + (double)i / OP), lo = ValAt(lon, kb + (double)i / OP);
                outline[2 * i] = (float)((lo - lo0) * cosL * 111320.0);
                outline[2 * i + 1] = (float)(-(la - la0) * 110540.0);
            }
            if (outline.Any(float.IsNaN)) outline = null;
        }
        return new SessionReviewData
        {
            SourceFile = ibt.Path, SourceSize = size, SourceWriteUtc = writeUtc,
            SessionStart = ibt.StartLocal.AddSeconds(Math.Max(0, time[r0] - ibt.StartTime)),
            TrackRaw = trackRaw, TrackDisplay = IbtFile.Yaml(y, "WeekendInfo", "TrackDisplayName"), TrackConfig = IbtFile.Yaml(y, "WeekendInfo", "TrackConfigName"),
            TrackCity = IbtFile.Yaml(y, "WeekendInfo", "TrackCity"), TrackCountry = IbtFile.Yaml(y, "WeekendInfo", "TrackCountry"), TrackLength = trackLen,
            TrackLat = Num(IbtFile.Yaml(y, "WeekendInfo", "TrackLatitude")), TrackLon = Num(IbtFile.Yaml(y, "WeekendInfo", "TrackLongitude")), TrackTurns = turns,
            Driver = me != null && me.TryGetValue("UserName", out var un) ? un : "", Car = me != null && me.TryGetValue("CarScreenName", out var cn) ? cn : "", CarClass = cls,
            SessionType = sType, IsRace = isRace,
            LapsCompleted = laps.Count, CleanLaps = laps.Count(l => l.Clean),
            BestLap = bestLap?.Time, BestLapNo = bestLap?.Number ?? 0,
            TypicalLap = cleanTimes.Count >= 2 ? Median(cleanTimes) : null, Theoretical = theo,
            StartClassPos = startPos, FinishClassPos = finishPos, FinishPos = finishAll, ClassSize = classSize,
            Incidents = (int)Math.Max(0, inc[r1] - inc[r0]), Offs = events.Count(e => e.Kind == 0), Spins = events.Count(e => e.Kind == 1),
            PitStops = pitStops, TopKmh = top > 0 ? (float)(top * 3.6) : null, AirC = Avg(air), TrackC = Avg(trk),
            DrivenMinutes = (time[r1] - time[r0]) / 60.0,
            Laps = laps.Select(l => new ReviewLap(l.Number, l.Time, l.Pit, l.Clean, l.Incidents, !l.Invalid)).ToList(),
            Segments = segs, Focus = focus, NamedCorners = named, Outline = outline,
        };
    }

    sealed class PassM
    {
        public double MinKmh, TopKmh, EntryKmh, ExitKmh, ApexU; public int Gear; public bool Edge;
        public double? BrakeU, BrakeToApex, ThrottleU, ThrottleAfterApex;
    }

    static double Num(string s)
    {
        var p = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return p.Length > 0 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    }

    record struct Piece(string Name, double A, double B, bool Straight);

    /// <summary>Named corners and the straights between them, as a gapless partition of the lap. Gaps under 250 m
    /// belong to the corner before them; longer ones are straights (named, or "Run to X").</summary>
    static List<Piece> Pieces(string trackRaw, string trackLen, double lenM, out bool named)
    {
        named = false;
        var list = TrackCorners.Corners(trackRaw);
        var outp = new List<Piece>();
        if (list == null || list.Count < 2) return outp;
        named = true;
        var cs = list.Select(c => (A: Math.Clamp(c.StartM, 0, lenM), B: Math.Clamp(Math.Max(c.EndM, c.StartM + 1), 0, lenM + 2000), c.Name)).ToList();
        for (int i = 0; i < cs.Count; i++)
        {
            var c = cs[i];
            double nextA = i + 1 < cs.Count ? cs[i + 1].A : cs[0].A + lenM;
            double end = Math.Min(c.B, nextA);
            double gap = nextA - end;
            if (gap < 250) { outp.Add(new Piece(c.Name, c.A / lenM, Wrap(nextA / lenM), false)); continue; }
            outp.Add(new Piece(c.Name, c.A / lenM, Wrap(end / lenM), false));
            double mid = Wrap((end + nextA) / 2 / lenM);
            string nm = TrackCorners.Straight(trackRaw, trackLen, (float)mid) ?? ("the run to " + (i + 1 < cs.Count ? cs[i + 1].Name : cs[0].Name));
            if (nm.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) nm = nm[4..];
            nm = char.ToUpperInvariant(nm[0]) + nm[1..];
            outp.Add(new Piece(nm, Wrap(end / lenM), Wrap(nextA / lenM), true));
        }
        return outp;
    }

    static double Wrap(double f) => f - Math.Floor(f);

    /// <summary>No landmarks for this track: corners found on the fastest lap's speed trace (every real slow-down),
    /// called "Corner 1", "Corner 2"... in lap order, with the straights between them.</summary>
    static List<Piece> PiecesFromSpeed(Func<double, double> kmhAt, double lenM)
    {
        var outp = new List<Piece>();
        int n = Math.Clamp((int)(lenM / 10), 200, 2000);              // a sample every ~10 m
        var v = new double[n];
        for (int i = 0; i < n; i++) v[i] = kmhAt((double)i / n);
        if (v.Any(double.IsNaN)) return outp;
        var sm = new double[n];
        for (int i = 0; i < n; i++) { double s = 0; for (int k = -3; k <= 3; k++) s += v[(i + k + n) % n]; sm[i] = s / 7; }
        int win = Math.Max(3, (int)(150 / (lenM / n))), back = Math.Max(5, (int)(400 / (lenM / n)));
        var apex = new List<int>();
        for (int i = 0; i < n; i++)
        {
            bool isMin = true;
            for (int k = -win; k <= win && isMin; k++) if (k != 0 && sm[(i + k + n) % n] < sm[i]) isMin = false;
            if (!isMin) continue;
            double mx = 0; for (int k = 1; k <= back; k++) mx = Math.Max(mx, sm[(i - k + n) % n]);
            if (mx - sm[i] >= 15 && (apex.Count == 0 || i - apex[^1] > win)) apex.Add(i);
        }
        if (apex.Count < 2) return outp;
        double step = 1.0 / n;
        var cs = apex.Select((ai, j) => (A: Wrap(ai * step - 120 / lenM), B: Wrap(ai * step + 80 / lenM), Name: "Corner " + (j + 1))).ToList();
        for (int i = 0; i < cs.Count; i++)
        {
            double a = cs[i].A, b = cs[i].B, na = cs[(i + 1) % cs.Count].A;
            double gap = Wrap(na - b) * lenM;
            if (gap < 250) { outp.Add(new Piece(cs[i].Name, a, na, false)); continue; }
            outp.Add(new Piece(cs[i].Name, a, b, false));
            outp.Add(new Piece("Run to " + cs[(i + 1) % cs.Count].Name, b, na, true));
        }
        return outp;
    }

    static double Median(List<double> sorted)
    {
        int n = sorted.Count;
        if (n == 0) return double.NaN;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
    }

    static double StdDev(List<double> v)
    {
        double m = v.Average();
        return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Count - 1));
    }

    /// <summary>One line of advice from the numbers: what the best pass did differently, the trouble there, or how
    /// steady it was. Thresholds keep it to real differences.</summary>
    public static string Tip(ReviewSegment s, bool imperial)
    {
        if (s.Passes == 0) return s.Offs + s.Spins > 0 ? "NO CLEAN PASS HERE THIS SESSION - GET IT TIDY FIRST" : "";
        var c = new List<(double Score, string Text)>();
        string U = imperial ? "MPH" : "KM/H";
        double Sp(double kmh) => Math.Round(imperial ? kmh / 1.609344 : kmh);
        if (s.Spins + s.Offs >= 1)
        {
            int n = s.Spins + s.Offs;
            c.Add((2.5 + n, (s.Spins > 0 ? (s.Spins == 1 ? "A SPIN" : s.Spins + " SPINS") : (s.Offs == 1 ? "AN OFF" : s.Offs + " OFFS"))
                            + " HERE - BANK A CLEAN LAP BEFORE CHASING TIME"));
        }
        if (!s.Straight)
        {
            if (s.MinKmhBest is { } mb && s.MinKmhTypical is { } mt && mb - mt >= 3)
                c.Add(((mb - mt) / 2.0, $"YOUR BEST CARRIED {Sp(mb - mt):0} {U} MORE TO THE APEX"));
            if (s.BrakeLaterBy is { } bl && bl >= 6 && bl <= 60)
                c.Add((bl / 10.0, $"YOUR BEST BRAKED {Math.Round(bl):0} M LATER"));
            if (s.BrakeLaterBy is { } be && be <= -6 && be >= -60 && s.MinKmhBest is { } mb2 && s.MinKmhTypical is { } mt2 && mb2 >= mt2)
                c.Add((-be / 12.0, $"YOUR BEST BRAKED {Math.Round(-be):0} M EARLIER AND CARRIED MORE SPEED"));
            if (s.ThrottleSoonerBy is { } ts && ts >= 8 && ts <= 150)
                c.Add((ts / 15.0, $"YOUR BEST WAS BACK TO FULL THROTTLE {Math.Round(ts):0} M SOONER"));
            if (s.MinKmhBest == null && s.ExitKmhBest is { } xb && s.ExitKmhTypical is { } xt && xb - xt >= 3)
                c.Add(((xb - xt) / 2.0, $"YOUR BEST LEFT IT {Sp(xb - xt):0} {U} FASTER"));
        }
        else
        {
            if (s.EntryKmhBest is { } eb && s.EntryKmhTypical is { } et && eb - et >= 3)
                c.Add(((eb - et) / 2.0, $"YOUR BEST STARTED THIS STRAIGHT {Sp(eb - et):0} {U} FASTER - IT'S ALL IN THE EXIT"));
            if (s.TopKmhBest is { } tb && s.TopKmhTypical is { } tt && tb - tt >= 3)
                c.Add(((tb - tt) / 2.5, $"YOUR BEST WAS {Sp(tb - tt):0} {U} FASTER AT THE END OF IT"));
        }
        if (!s.Straight && s.Spread is { } sd && sd >= 0.25 && s.Passes >= 4)
            c.Add((sd * 4, $"LAP TO LAP IT VARIES BY {sd:0.00} S - SAME MARKERS EVERY LAP"));
        if (c.Count == 0)
        {
            if (s.Gain is { } g && g < 0.05 && s.Passes >= 3) return "CONSISTENT - WITHIN A FEW HUNDREDTHS OF YOUR BEST HERE";
            return "";
        }
        return c.OrderByDescending(x => x.Score).First().Text;
    }

    /// <summary>Saves a review next to the others (one JSON per telemetry file).</summary>
    public static void Save(SessionReviewData d, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(d, SessionReviewData.Json));
        File.Move(tmp, file, true);
    }

    public static SessionReviewData? Load(string file)
    {
        try
        {
            var d = JsonSerializer.Deserialize<SessionReviewData>(File.ReadAllText(file), SessionReviewData.Json);
            return d is { Version: SessionReviewData.CurrentVersion } ? d : null;
        }
        catch { return null; }
    }
}
