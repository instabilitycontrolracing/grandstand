using System.Diagnostics;
using ApexHUD.Native.Common;
using IRSDKSharper;

namespace ApexHUD.Native.Data;

/// <summary>
/// The keystone every data-driven overlay binds to. Connects to iRacing via
/// IRSDKSharper, reshapes the shared-memory feed into an immutable
/// <see cref="RaceSnapshot"/>, and publishes a fresh snapshot on every
/// telemetry tick. Replaces the entire legacy
/// <c>iRacing → SimHub + benofficial2 → server.py → NewData JSON</c> chain
/// with one in-process service (see DECISIONS.md, "Data-layer translation").
/// </summary>
/// <remarks>
/// Threading: <see cref="OnSessionInfo"/> and <see cref="OnTelemetryData"/>
/// fire on two separate IRSDKSharper background threads. The session cache is
/// updated under a lock; telemetry reads it lock-free via <see cref="Volatile"/>.
/// Subscribers run inside the telemetry-thread callback — keep handlers cheap
/// or marshal onto the UI thread yourself.
/// </remarks>
public sealed partial class RaceStateService : IDisposable
{
    readonly IRacingSdk irsdk = new();

    // SessionInfo cache, swapped wholesale on each YAML event.
    SessionCache session = SessionCache.Empty;
    readonly object sessionLock = new();

    // Hot telemetry datum cache (resolved lazily once the SDK is ready); skips
    // the dictionary lookup on every read.
    DatumCache? datums;
    readonly TrackMapRecorder mapRecorder = new();

    // Re-used per-tick buffers so we don't allocate ~10 arrays of 64 ints every frame.
    readonly int[]   lapBuf            = new int[IRacingSdkConst.MaxNumCars];
    readonly int[]   lapsCompletedBuf  = new int[IRacingSdkConst.MaxNumCars];
    readonly float[] lapDistPctBuf     = new float[IRacingSdkConst.MaxNumCars];
    readonly float[] bestLapTimeBuf    = new float[IRacingSdkConst.MaxNumCars];
    readonly float[] lastLapTimeBuf    = new float[IRacingSdkConst.MaxNumCars];
    readonly int[]   positionBuf       = new int[IRacingSdkConst.MaxNumCars];
    readonly int[]   classPositionBuf  = new int[IRacingSdkConst.MaxNumCars];
    readonly bool[]  onPitRoadBuf      = new bool[IRacingSdkConst.MaxNumCars];
    readonly int[]   trackSurfaceBuf   = new int[IRacingSdkConst.MaxNumCars];
    // 2026-10-24: iRacing's own timing - CarIdxF2Time (race: time behind the leader;
    // otherwise the car's fastest lap) and CarIdxEstTime (estimated time from the line
    // to the car's spot on this lap). Optional channels; buffers stay -1 when absent.
    readonly float[] f2TimeBuf         = new float[IRacingSdkConst.MaxNumCars];
    readonly float[] estTimeBuf        = new float[IRacingSdkConst.MaxNumCars];

    // Telemetry-dropout hold. iRacing intermittently reports lap=-1 /
    // trackSurface=-1 / lapDistPct=-1 for cars that are genuinely on track (a
    // known quirk in larger fields). We cache each car's last-known-good
    // sample and, within a short grace window, hold a momentarily-invalid car
    // at that sample so it doesn't flicker disconnected (which blanked its gap
    // and disrupted the gaps of cars behind it). Touched only on the telemetry
    // thread inside BuildSnapshot, so no locking needed.
    readonly Dictionary<int, CarHold> carHold = new();
    const long HoldGraceMs = 2500;

    struct CarHold
    {
        public int Lap;
        public float LapDistPct;
        public int LapsCompleted;
        public int Position;
        public int ClassPosition;
        public int TrackSurface;
        public float Best;
        public float Last;
        public long LastValidMs;
        public string? Who;          // the driver this sample belongs to (a car slot can be reused by someone else)
    }
    int holdSessionNum = int.MinValue;   // the hold never carries laps from one session into the next
    double lastSessionTime = -1;
    string greenSeenKey = ""; bool greenPending;   // #405: waiting for this race session's green
    long subSessionId;               // for the sector timing file (2026-10-24)
    string keyCache = ""; long keySub = -1; int keyNum = int.MinValue;
    string KeyFor(int num)
    {
        if (num != keyNum || subSessionId != keySub) { keyNum = num; keySub = subSessionId; keyCache = $"{subSessionId}:{num}"; }
        return keyCache;
    }
    int replayJumps;
    /// <summary>Counts replay jumps (session clock moved back, or forward by more than a few seconds at once).
    /// The overlay resets its race diary when this changes.</summary>
    public int ReplayJumps => Volatile.Read(ref replayJumps);
    /// <summary>True while watching a replay (iRacing's IsReplayPlaying).</summary>
    public bool IsReplay { get; private set; }
    /// <summary>Every iRacing class in the session and the label it got, e.g.
    /// "4029 'GTP' -> GTP (short)". Rebuilt on each session-info update; the host logs it when it changes.</summary>
    public string ClassMapReport { get; private set; } = "";

    /// <summary>Most recent snapshot. Disconnected sentinel until first tick.</summary>
    public RaceSnapshot Current { get; private set; } = RaceSnapshot.Disconnected;

    /// <summary>Fires after <see cref="Current"/> is replaced. Runs on the telemetry thread.</summary>
    public event Action<RaceSnapshot>? SnapshotUpdated;

    /// <summary>Fires when an exception is caught in any background task.</summary>
    public event Action<Exception>? Exception;

    public RaceStateService()
    {
        irsdk.OnConnected      += OnConnected;
        irsdk.OnDisconnected   += OnDisconnected;
        irsdk.OnSessionInfo    += OnSessionInfo;
        irsdk.OnTelemetryData  += OnTelemetryData;
        irsdk.OnException      += OnSdkException;
        // UpdateInterval=1 → fire OnTelemetryData on every 60 Hz frame, no
        // throttle. The snapshot build is cheap; broadcast smoothness comes
        // from feeding overlays full-rate live gaps (see DECISIONS.md).
        irsdk.UpdateInterval = 1;
    }

    /// <summary>One iRacing telemetry variable with its live value, for the Grandstand control app's data browser.</summary>
    public sealed record TelemetryVar(string Name, string Desc, string Unit, int Count, string Value);

    /// <summary>
    /// Every telemetry variable iRacing publishes, with its current value (2026-10-23, control app).
    /// Called only when the data browser is open (about once a second, on the control
    /// server's thread) - never from the render loop. Empty when iRacing is not running.
    /// </summary>
    public IReadOnlyList<TelemetryVar> ListTelemetry()
    {
        var list = new List<TelemetryVar>(400);
        try
        {
            var data = irsdk.Data;
            if (!sdkConnected || !data.TelemetryDataPropertiesReady) return list;
            foreach (var d in data.TelemetryDataProperties.Values)
            {
                string v;
                try
                {
                    if (d.Count <= 1) v = Fmt(data.GetValue(d, 0));
                    else
                    {
                        int n = Math.Min(d.Count, 6);
                        var parts = new string[n];
                        for (int i = 0; i < n; i++) parts[i] = Fmt(data.GetValue(d, i));
                        v = "[" + string.Join(", ", parts) + (d.Count > n ? ", ...]" : "]");
                    }
                }
                catch { v = "-"; }
                list.Add(new TelemetryVar(d.Name, d.Desc, d.Unit, d.Count, v));
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }
        catch { }
        return list;
    }

    static string Fmt(object? o) => o switch
    {
        null => "-",
        float f => f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        double d => d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        bool b => b ? "yes" : "no",
        _ => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) ?? "-",
    };

    // IRSDKSharper stops its background task after an exception until Stop/Start is called
    // (its own docs). Restart it a moment later so the feed never freezes silently mid-race.
    int restarting;
    void OnSdkException(Exception ex)
    {
        Exception?.Invoke(ex);
        if (Interlocked.Exchange(ref restarting, 1) == 1) return;
        System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(1500);
                try { irsdk.Stop(); } catch { }
                await System.Threading.Tasks.Task.Delay(500);
                irsdk.Start();
            }
            catch (Exception e2) { Exception?.Invoke(e2); }
            finally { Interlocked.Exchange(ref restarting, 0); }
        });
    }

    public void Start() => irsdk.Start();
    public void Stop() => irsdk.Stop();

    public void Dispose()
    {
        irsdk.OnConnected     -= OnConnected;
        irsdk.OnDisconnected  -= OnDisconnected;
        irsdk.OnSessionInfo   -= OnSessionInfo;
        irsdk.OnTelemetryData -= OnTelemetryData;
        irsdk.Stop();
    }

    void OnConnected()
    {
        // Datum resolution is deferred to the first telemetry tick. The
        // telemetry variable headers (TelemetryDataProperties) are NOT
        // guaranteed populated at OnConnected time — only once frames begin
        // arriving. Resolving here threw "CarIdxLap not present" and left
        // datums null forever, so the service published only the Disconnected
        // sentinel. Resolved lazily in OnTelemetryData instead.
        datums = null;
        mapRecorder.Invalidate();
    }

    void OnDisconnected()
    {
        sdkConnected = false;
        datums = null;
        carHold.Clear();
        holdSessionNum = int.MinValue;
        Volatile.Write(ref splashInfo, null);   // 2026-10-27: the loading splash never shows the last sim run's track
        DrivingState.Disconnected();
        Publish(RaceSnapshot.Disconnected);
    }

    void OnSessionInfo()
    {
        try
        {
            var info = irsdk.Data.SessionInfo;
            CaptureSplash(info);   // 2026-10-27: loading-splash fields, in their own guard
            var weekend = info.WeekendInfo;
            var drivers = info.DriverInfo;
            var currentSessionNum = info.SessionInfo?.CurrentSessionNum ?? 0;
            string? sessionType = null;
            // 2026-10-18: keep EVERY session's type (0=Practice, 1=Lone Qualify, 2=Race ...)
            // so BuildSnapshot can resolve the current one from the LIVE telemetry
            // SessionNum each tick, instead of trusting only the YAML CurrentSessionNum
            // captured at the last session-info update (battle mode is race-only, so a
            // stale "Practice" here silently disables it for the whole race).
            var sessionTypes = new Dictionary<int, string>();
            var sessionLaps = new Dictionary<int, int>();
            var sessionLengths = new Dictionary<int, double>();
            var officialByNum = new Dictionary<int, bool>();
            var dqByNum = new Dictionary<int, HashSet<int>>();
            foreach (var s in info.SessionInfo?.Sessions ?? new())
            {
                sessionTypes[s.SessionNum] = s.SessionType ?? string.Empty;
                sessionLaps[s.SessionNum] = int.TryParse(s.SessionLaps, out var sl) ? sl : 0;
                sessionLengths[s.SessionNum] = ParseSeconds(s.SessionTime?.ToString());
                if (s.SessionNum == currentSessionNum) sessionType = s.SessionType;
                officialByNum[s.SessionNum] = Convert.ToString(s.ResultsOfficial, System.Globalization.CultureInfo.InvariantCulture) == "1";
                // 2026-10-25 (Angelo: the Trading Paints card for every DQ): who iRacing has disqualified in this session
                var dq = new HashSet<int>();
                try
                {
                    foreach (var rp in s.ResultsPositions ?? new())
                        if ((rp.ReasonOutStr ?? "").Contains("isqual", StringComparison.OrdinalIgnoreCase)) dq.Add(rp.CarIdx);
                }
                catch { }
                dqByNum[s.SessionNum] = dq;
            }

            subSessionId = weekend.SubSessionID;
            // 2026-10-26 (#235): a saved replay file ("replay") vs a live session ("full") - the replay wipe only says LIVE in a live one
            ReplayState.SetReplayFile(string.Equals(weekend.SimMode, "replay", StringComparison.OrdinalIgnoreCase));
            try
            {
                var secs = info.SplitTimeInfo?.Sectors;
                if (secs != null && secs.Count >= 2)
                    SectorTimer.SetSectors(secs.Select(x => (float)x.SectorStartPct).ToList());
            }
            catch { /* thirds stay */ }

            var driverByCarIdx = new Dictionary<int, DriverStatic>();
            var classMap = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in drivers.Drivers)
            {
                if (d.CarIsPaceCar != 0) continue;
                // 2026-10-24: class from iRacing's CarClassID / CarClassShortName (car-name fallback only for unknowns)
                // 2026-10-29 (car parity): the car's sourced fact sheet (CarID, then CarPath) for its class, maker and name
                var car = CarCatalog.Find(d.CarID, d.CarPath);
                var cls = ClassDetection.Resolve(d.CarClassID, d.CarClassShortName, FixMojibake(d.UserName), CleanCarName(d.CarScreenName), car?.ClassLabel);
                classMap.Add($"{d.CarClassID} '{d.CarClassShortName}' -> {cls.Label} ({cls.Source})");
                driverByCarIdx[d.CarIdx] = new DriverStatic(
                    Name: FixMojibake(d.UserName),
                    // iRacing's server-built AbbrevName / Initials can arrive garbled AND
                    // cut mid-character (logged: "LÃ³pez, Ã" for "Òscar López López"),
                    // which can't be repaired. When garbled, drop it so NameFormat builds
                    // "Ò. LÓPEZ" from the (correct) full UserName instead.
                    Abbrev: LooksGarbled(d.AbbrevName) ? string.Empty : FixMojibake(d.AbbrevName),
                    Initials: LooksGarbled(d.Initials) ? string.Empty : FixMojibake(d.Initials),
                    CarNumber: d.CarNumber ?? string.Empty,
                    CarName: CarNameFor(d.CarScreenName, car),
                    CarBrand: BrandFor(d.CarScreenName, car),
                    ClassColor: NormalizeHex(d.CarClassColor),
                    LicString: d.LicString ?? string.Empty,
                    LicColor: NormalizeHex(d.LicColor),
                    IRating: d.IRating,
                    IsSpectator: d.IsSpectator != 0,
                    IsPaceCar: d.CarIsPaceCar != 0,
                    Incidents: d.CurDriverIncidentCount,
                    ClassEstLap: d.CarClassEstLapTime,
                    ClassLabel: cls.Label,
                    CarPath: d.CarPath ?? string.Empty,
                    UserId: d.UserID,
                    TeamId: d.TeamID,
                    CarDesign: d.CarDesignStr ?? string.Empty,
                    NumberDesign: d.CarNumberDesignStr ?? string.Empty,
                    Sponsors: $"{d.CarSponsor_1},{d.CarSponsor_2}",
                    CarId: d.CarID);
            }
            ClassMapReport = string.Join(", ", classMap.Order(StringComparer.Ordinal));

            // Starting grid from the qualifying result. iRacing publishes no
            // live running order before the green (CarIdxPosition reads 0 for
            // every car on the grid / formation lap — verified on a Spa replay),
            // so we capture each car's qualifying place here and use it as the
            // pre-green fallback in BuildSnapshot. QualifyResultsInfo
            // Position/ClassPosition are 0-indexed (pole = 0); store 1-indexed
            // to line up with live telemetry positions.
            var grid = new Dictionary<int, (int Pos, int ClassPos)>();
            var qResults = info.QualifyResultsInfo?.Results;
            var qualTimes = new Dictionary<int, float>();
            if (qResults != null)
                foreach (var qr in qResults)
                {
                    grid[qr.CarIdx] = (qr.Position + 1, qr.ClassPosition + 1);
                    if (qr.FastestTime > 0) qualTimes[qr.CarIdx] = (float)qr.FastestTime;
                }

            var newSession = new SessionCache(
                PlayerCarIdx: drivers.DriverCarIdx,
                Drivers: driverByCarIdx,
                TrackRawName: weekend.TrackName ?? string.Empty,
                TrackDisplayName: weekend.TrackDisplayName ?? string.Empty,
                TotalLaps: ParseTotalLaps(info.SessionInfo, currentSessionNum),
                SessionTypeName: sessionType ?? string.Empty,
                IsRaining: false,          // live: WeatherDeclaredWet each tick (TrackPrecipitation is the forecast chance)
                SeriesId: weekend.SeriesID > 0 ? weekend.SeriesID : null,
                Grid: grid)
            {
                SessionTypes = sessionTypes, QualTimes = qualTimes,
                SessionLapsByNum = sessionLaps, SessionLengthByNum = sessionLengths, OfficialByNum = officialByNum, DqByNum = dqByNum,
                RainChance = ParsePct(weekend.TrackPrecipitation),
                TrackLength = weekend.TrackLength ?? string.Empty,
                TrackTurns = weekend.TrackNumTurns,
                TrackCity = weekend.TrackCity ?? string.Empty,
                TrackConfigName = weekend.TrackConfigName ?? string.Empty,
                TrackCountry = weekend.TrackCountry ?? string.Empty,
                TrackLat = ParseCoord(weekend.TrackLatitude?.ToString()),
                TrackLon = ParseCoord(weekend.TrackLongitude?.ToString()),
                TrackNorthOffset = ParseCoord(weekend.TrackNorthOffset?.ToString()),
                CamGroups = ReadCamGroups(info),
                CamNames = ReadCamNames(info),
                ShiftRpm = SafeF(() => (float)drivers.DriverCarSLShiftRPM),
                RedlineRpm = SafeF(() => (float)drivers.DriverCarRedLine),
            };

            lock (sessionLock) session = newSession;
        }
        catch (Exception ex) { Exception?.Invoke(ex); }
    }

    // ---- Session-live signal (2026-10-18) -------------------------------------
    // True only while real telemetry frames are flowing from a loaded session -
    // the same signal SimHub/CrewChief key off. The sim WINDOW exists during the
    // loading screen; telemetry frames don't, so the overlay can stay hidden
    // until the session is actually live.
    long lastTelemetryTick;
    volatile bool sdkConnected;

    /// <summary>True while a session is loaded and telemetry is arriving (frames in the last 2 s).</summary>
    public bool IsSessionLive =>
        sdkConnected && (Environment.TickCount64 - Interlocked.Read(ref lastTelemetryTick)) < 2000;

    void OnTelemetryData()
    {
        sdkConnected = true;
        Interlocked.Exchange(ref lastTelemetryTick, Environment.TickCount64);
        try
        {
            // Lazily resolve the datum cache on the first tick whose telemetry
            // headers are ready. OnConnected can fire before
            // TelemetryDataProperties is populated, so retry here until
            // resolution succeeds, then re-use the cache forever.
            if (datums is null)
            {
                try { datums = DatumCache.Resolve(irsdk.Data); }
                catch { return; } // headers not ready yet — try again next tick
            }
            if (datums is not { } d) return;
            var snap = BuildSnapshot(d);
            Publish(snap);
            // trace the track map from the player's own laps until this track has one
            mapRecorder.Tick(irsdk.Data, Volatile.Read(ref session).TrackDisplayName);
        }
        catch (Exception ex) { Exception?.Invoke(ex); }
    }

    RaceSnapshot BuildSnapshot(DatumCache d)
    {
        var data = irsdk.Data;
        var s = Volatile.Read(ref session);
        int myIncidents = -1;
        try { if (d.PlayerIncidents != null) myIncidents = data.GetInt(d.PlayerIncidents); } catch { myIncidents = -1; }

        // Telemetry array reads (one syscall-ish each via the cached Datum).
        data.GetIntArray   (d.CarIdxLap,            lapBuf,           0, lapBuf.Length);
        data.GetIntArray   (d.CarIdxLapCompleted,   lapsCompletedBuf, 0, lapsCompletedBuf.Length);
        data.GetFloatArray (d.CarIdxLapDistPct,     lapDistPctBuf,    0, lapDistPctBuf.Length);
        data.GetFloatArray (d.CarIdxBestLapTime,    bestLapTimeBuf,   0, bestLapTimeBuf.Length);
        data.GetFloatArray (d.CarIdxLastLapTime,    lastLapTimeBuf,   0, lastLapTimeBuf.Length);
        data.GetIntArray   (d.CarIdxPosition,       positionBuf,      0, positionBuf.Length);
        data.GetIntArray   (d.CarIdxClassPosition,  classPositionBuf, 0, classPositionBuf.Length);
        data.GetBoolArray  (d.CarIdxOnPitRoad,      onPitRoadBuf,     0, onPitRoadBuf.Length);
        data.GetIntArray   (d.CarIdxTrackSurface,   trackSurfaceBuf,  0, trackSurfaceBuf.Length);
        if (d.CarIdxF2Time != null) data.GetFloatArray(d.CarIdxF2Time, f2TimeBuf, 0, f2TimeBuf.Length);
        else Array.Fill(f2TimeBuf, -1f);
        if (d.CarIdxEstTime != null) data.GetFloatArray(d.CarIdxEstTime, estTimeBuf, 0, estTimeBuf.Length);
        else Array.Fill(estTimeBuf, -1f);

        var sessionState     = data.GetInt(d.SessionState);

        // 2026-10-18: resolve the CURRENT session's type from the live telemetry
        // SessionNum (updated every tick) rather than the YAML CurrentSessionNum
        // captured at the last session-info update. Every downstream use of
        // s.SessionTypeName (battle mode's race gate, race-vs-practice tower
        // ordering, phase) then follows the live session automatically.
        int liveSessionNum = data.GetInt(d.SessionNum);
        if (liveSessionNum != holdSessionNum)
        {
            carHold.Clear();                 // practice laps must not show as qualifying / race laps
            holdSessionNum = liveSessionNum;
            lastSessionTime = -1;
            SectorTimer.UseSession($"{subSessionId}:{liveSessionNum}");   // per session; comes back after a restart
        }
        // replay rewinds / skips: the session clock jumps
        if (d.IsReplayPlaying != null) IsReplay = data.GetBool(d.IsReplayPlaying);
        try
        {
            ReplayState.Set(IsReplay, d.ReplayPlaySpeed != null ? data.GetInt(d.ReplayPlaySpeed) : 1,
                            d.ReplayPlaySlowMotion != null && data.GetBool(d.ReplayPlaySlowMotion));
        }
        catch { ReplayState.Set(IsReplay, 1, false); }
        // 2026-10-26 (#235): how far the replay is behind live (iRacing ReplayFrameNumEnd, 60 frames a second; ~0 = at live)
        // 2026-10-29: live in the car iRacing can report ReplayFrameNum 0 with ReplayFrameNumEnd counting UP (129074 seen while
        // driving) - the tape isn't positioned, so that number isn't "behind live". No tape position + no replay playing = live.
        try
        {
            int end = d.ReplayFrameNumEnd != null ? data.GetInt(d.ReplayFrameNumEnd) : -1;
            int num = d.ReplayFrameNum != null ? data.GetInt(d.ReplayFrameNum) : -1;
            ReplayState.SetFramesBehind(end >= 0 && num == 0 && !IsReplay ? 0 : end);
        }
        catch { ReplayState.SetFramesBehind(-1); }
        // 2026-10-27 (#338): is the owner driving live? story cards are held in battles while he is (#338, DrivingState).
        try
        {
            bool B(IRacingSdkDatum? x) { try { return x != null && data.GetBool(x); } catch { return false; } }
            DrivingState.Update(B(d.IsOnTrack), B(d.IsOnTrackCar), IsReplay, ReplayState.FramesBehindLive,
                             ReplayState.IsReplayFile, Environment.TickCount64);
        }
        catch { }
        if (d.SessionTime != null)
        {
            double st = data.GetDouble(d.SessionTime);
            if (lastSessionTime >= 0 && (st < lastSessionTime - 1.0 || st > lastSessionTime + 5.0))
            {
                Interlocked.Increment(ref replayJumps);
                ReplayState.Jumped();
                carHold.Clear();
                SectorTimer.Reset();
            }
            lastSessionTime = st;
            ReplayState.SetTime(st);
        }
        // rain = the sim's live "declared wet" state, not the forecast chance in the session info
        if (d.WeatherDeclaredWet != null)
        {
            bool wetNow = data.GetBool(d.WeatherDeclaredWet);
            if (wetNow != s.IsRaining)
            {
                var wetUpd = s with { IsRaining = wetNow };
                lock (sessionLock) { if (ReferenceEquals(session, s)) session = wetUpd; }
                s = wetUpd;
            }
        }
        if (s.SessionTypes.TryGetValue(liveSessionNum, out var liveType)
            && !string.Equals(liveType, s.SessionTypeName, StringComparison.Ordinal))
        {
            var updated = s with { SessionTypeName = liveType };
            lock (sessionLock) { if (ReferenceEquals(session, s)) session = updated; }
            s = updated;
        }
        var sessionFlagsBits = data.GetBitField(d.SessionFlags);
        var camCarIdx        = data.GetInt(d.CamCarIdx);
        var currentLap       = data.GetInt(d.Lap);
        var sessionTimeRemain= data.GetDouble(d.SessionTimeRemain);
        var sessionTimeOfDay = data.GetFloat(d.SessionTimeOfDay);
        var airTemp          = data.GetFloat(d.AirTemp);
        var trackTemp        = data.GetFloat(d.TrackTempCrew);

        // First pass: build raw rows for valid cars and compute progress.
        var raw = new List<RawRow>(capacity: 32);
        int leaderCarIdx = -1;
        int leaderPosition = int.MaxValue;
        double? leaderProgress = null;
        float? leaderBest = null;

        long nowMs = Environment.TickCount64;

        for (int idx = 0; idx < IRacingSdkConst.MaxNumCars; idx++)
        {
            if (!s.Drivers.TryGetValue(idx, out var stat)) continue;
            if (stat.IsPaceCar) continue;

            int trackSurf = trackSurfaceBuf[idx];
            int lap       = lapBuf[idx];
            float pct     = lapDistPctBuf[idx];
            int lapsComp  = lapsCompletedBuf[idx];
            int pos       = positionBuf[idx];
            int classPos  = classPositionBuf[idx];
            float best    = bestLapTimeBuf[idx];
            float last    = lastLapTimeBuf[idx];
            // 2026-10-24 (Angelo): iRacing can leave CarIdxBestLapTime blank for a lap or two (seen in replays: BEST LAP
            // stayed "—" until lap 4). While it's blank, use the fastest completed lap we've seen for this car.
            if (best <= 0f && last > 30f && carHold.TryGetValue(idx, out var seen) && seen.Who == stat.Name && seen.Best > 0f)
                best = Math.Min(seen.Best, last);
            else if (best <= 0f && last > 30f)
                best = last;

            // Apply the telemetry-dropout hold. A frame is "live valid" when
            // the car reports as on-track with a real lap distance. When valid
            // we refresh the cache; when momentarily invalid but seen good
            // within the grace window we restore the cached sample so the car
            // stays put and connected instead of flickering away.
            bool liveValid = trackSurf >= 0 && pct >= 0f && lap >= 0;
            if (liveValid)
            {
                carHold.TryGetValue(idx, out var prev);
                if (prev.Who != stat.Name) prev = default;   // someone else in this car slot now
                if (best <= 0) best = prev.Best;   // keep a known best/last if
                if (last <= 0) last = prev.Last;   // this frame's reads blank
                carHold[idx] = new CarHold
                {
                    Lap = lap, LapDistPct = pct, LapsCompleted = lapsComp,
                    Position = pos, ClassPosition = classPos, TrackSurface = trackSurf,
                    Best = best, Last = last, LastValidMs = nowMs, Who = stat.Name,
                };
            }
            else if (carHold.TryGetValue(idx, out var hold)
                     && hold.Who == stat.Name
                     && nowMs - hold.LastValidMs <= HoldGraceMs)
            {
                lap       = hold.Lap;
                pct       = hold.LapDistPct;
                lapsComp  = hold.LapsCompleted;
                pos       = pos > 0 ? pos : hold.Position;
                classPos  = classPos > 0 ? classPos : hold.ClassPosition;
                trackSurf = hold.TrackSurface;     // >= 0 → treated connected
                if (best <= 0) best = hold.Best;
                if (last <= 0) last = hold.Last;
            }

            // Pre-green grid fallback. Before the race goes green
            // (SessionState < Racing=4) iRacing reports no live running order —
            // CarIdxPosition / CarIdxClassPosition are 0 for every car on the
            // grid and formation lap — which rendered every starting-grid slot
            // as a dash and left the field in car-index order. Fill each car's
            // qualifying place (captured in OnSessionInfo) only while pre-green
            // and only where the live value is still missing, so the instant the
            // race starts the live order takes over and the post-race finishing
            // order (SessionState >= 4) is never overwritten.
            if (sessionState < 4 && s.Grid.TryGetValue(idx, out var gridPos))
            {
                if (pos <= 0)      pos = gridPos.Pos;
                if (classPos <= 0) classPos = gridPos.ClassPos;
            }

            // pos==0 typically means "not in session"; trackSurface==-1 means
            // not in world. Either way (and once any hold has expired) we
            // exclude from the leaderboard.
            if (pos <= 0 && trackSurf < 0) continue;

            var prog = LiveGap.Progress(lap, pct);
            raw.Add(new RawRow(idx, stat, pos, classPos,
                lap, pct, lapsComp, best, last,
                onPitRoadBuf[idx], trackSurf, prog));

            if (pos > 0 && pos < leaderPosition)
            {
                leaderPosition = pos;
                leaderCarIdx = idx;
                leaderProgress = prog;
                leaderBest = best > 0 ? best : null;
            }
        }

        // Reference pace (seconds/lap) for gap math when a car has no best lap
        // yet (early race): the fastest best lap in the field, else the fastest
        // last lap. Keeps live gaps visible from lap 1 instead of blanking to
        // "—" until a best lap is set, and self-corrects to real best-lap pace
        // the moment one exists.
        float sessionRefPace = 0f;
        foreach (var rr in raw)
            if (rr.BestLap > 0 && (sessionRefPace == 0f || rr.BestLap < sessionRefPace))
                sessionRefPace = rr.BestLap;
        if (sessionRefPace == 0f)
            foreach (var rr in raw)
                if (rr.LastLap > 0 && (sessionRefPace == 0f || rr.LastLap < sessionRefPace))
                    sessionRefPace = rr.LastLap;

        // Per-car pace with fallback: own best lap → own last lap → session
        // reference pace → null (no time-based gap possible at all).
        float? PaceFor(float best, float last)
            => best > 0f ? best
             : last > 0f ? last
             : sessionRefPace > 0f ? sessionRefPace
             : (float?)null;

        // Group by detected class label; preserve discovery order via list.
        var classGroups = new Dictionary<string, List<Driver>>(StringComparer.Ordinal);
        var classOrder = new List<string>();
        var classColors = new Dictionary<string, string>(StringComparer.Ordinal);

        // Sort raw by overall position so that within a class the natural
        // PositionInClass order matches and the first-seen driver carries the
        // class color/name.
        raw.Sort(static (a, b) =>
        {
            int pa = a.Position > 0 ? a.Position : int.MaxValue;
            int pb = b.Position > 0 ? b.Position : int.MaxValue;
            return pa.CompareTo(pb);
        });

        // Leader pass (sorted) — overall leader is raw[0] if it has a real position.
        if (raw.Count > 0 && raw[0].Position > 0)
        {
            leaderCarIdx = raw[0].CarIdx;
            leaderProgress = raw[0].Progress;
            leaderBest = raw[0].BestLap > 0 ? raw[0].BestLap : null;
        }

        Driver? player = null, leader = null, camera = null;
        SectorTimer.MaybeSave();

        foreach (var r in raw)
        {
            var stat = r.Stat;
            var className = stat.ClassLabel;
            if (lastSessionTime >= 0) SectorTimer.Update(r.CarIdx, className, r.LapDistPct, lastSessionTime, r.OnPitRoad);
            // Laps down is computed PER CLASS below (against the class leader),
            // not here against the overall leader. In multiclass racing the
            // overall leader is the fastest class (e.g. GTP), so measuring the
            // whole GT3 field against it falsely tagged every GT3 car "+1L"
            // even when they were on the lead lap of their own class. Start at
            // 0 and let the per-class interval pass fill it in.
            int lapDifference = 0;

            // Gap to the CLASS leader is filled in the per-class pass below, from
            // iRacing's own timing (CarIdxF2Time) - see ClassGaps (2026-10-24).
            double? gapToLeader = null;

            var drv = new Driver(
                CarIdx: r.CarIdx,
                Name: stat.Name,
                AbbrevName: stat.Abbrev,
                Initials: stat.Initials,
                CarNumber: stat.CarNumber,
                CarName: stat.CarName,
                CarBrand: stat.CarBrand,
                ClassName: className,
                ClassColor: ClassColors.For(className, stat.ClassColor),
                LicenceString: stat.LicString,
                LicenceColor: stat.LicColor,
                Position: r.Position,
                PositionInClass: r.ClassPosition,
                CurrentLap: r.CurrentLap,
                LapDistPct: r.LapDistPct,
                LapsCompleted: r.LapsCompleted,
                LapDifference: lapDifference,
                BestLapTime: r.BestLap,
                LastLapTime: r.LastLap,
                GapToLeaderSeconds: gapToLeader,
                IntervalSeconds: null, // filled below per-class (ClassGaps)
                IsInPit: r.OnPitRoad,
                IsConnected: r.TrackSurface >= 0,
                IsPaceCar: stat.IsPaceCar,
                IsSpectator: stat.IsSpectator,
                IRating: stat.IRating,
                QualifyTime: s.QualTimes.TryGetValue(r.CarIdx, out var qTime) ? qTime : 0f,
                Incidents: r.CarIdx == s.PlayerCarIdx && myIncidents >= 0 ? myIncidents : stat.Incidents,
                OffTrack: r.TrackSurface == 0,
                CarPath: stat.CarPath, UserId: stat.UserId, TeamId: stat.TeamId,
                CarDesign: stat.CarDesign, NumberDesign: stat.NumberDesign, Sponsors: stat.Sponsors,
                Disqualified: s.DqByNum.TryGetValue(liveSessionNum, out var dqs) && dqs.Contains(r.CarIdx),
                CarId: stat.CarId);

            if (!classGroups.TryGetValue(className, out var bucket))
            {
                bucket = new List<Driver>(capacity: 16);
                classGroups[className] = bucket;
                classOrder.Add(className);
                classColors[className] = ClassColors.For(className, stat.ClassColor);
            }
            bucket.Add(drv);

            if (r.CarIdx == s.PlayerCarIdx) player = drv;
            if (r.CarIdx == leaderCarIdx) leader = drv;
            if (r.CarIdx == camCarIdx) camera = drv;
        }

        // Per-class: sort by class position, fill interval gaps.
        var classes = new List<DriverClass>(classOrder.Count);
        foreach (var className in classOrder)
        {
            var bucket = classGroups[className];
            // 2026-10-18 (Angelo): drivers with NO position (no lap time yet in
            // practice/quali -> iRacing reports 0) used to sort as 0 = TOP of the
            // class. They now go to the BOTTOM, below everyone with a real place,
            // ordered by car number so they don't shuffle around.
            bucket.Sort(static (a, b) =>
            {
                static int Key(Driver d) =>
                    d.PositionInClass > 0 ? d.PositionInClass
                  : d.Position > 0        ? 100_000 + d.Position
                  :                         int.MaxValue;
                int c = Key(a).CompareTo(Key(b));
                if (c != 0) return c;
                int na = int.TryParse(a.CarNumber, out var x) ? x : int.MaxValue;
                int nb = int.TryParse(b.CarNumber, out var y) ? y : int.MaxValue;
                c = na.CompareTo(nb);
                return c != 0 ? c : a.CarIdx.CompareTo(b.CarIdx);
            });

            // 2026-10-18 (Angelo): practice / qualifying / warmup are ordered by
            // FASTEST LAP (like real timing screens); races keep the live running
            // order above. The shown position becomes that best-lap rank; drivers
            // with no lap yet stay at the bottom with a dash (position 0).
            var stName = s.SessionTypeName ?? string.Empty;
            bool timedOrder = stName.Length > 0
                && stName.IndexOf("race", StringComparison.OrdinalIgnoreCase) < 0;
            // 2026-10-24 (Angelo): lap 1 of a race - nobody has crossed the line yet, so iRacing's
            // scored order is still the grid. Order the tower by where the cars actually are on the
            // road (lap + distance round it); the times stay hidden until lap 1 is done (tower renderer).
            // 2026-10-24 (Angelo): "the tower only updates places at the start/finish line" - iRacing's scored
            // positions only change when a car crosses the line, so for the whole race (not just lap 1) the
            // order comes from where the cars are on the road (laps + distance round the lap). Cars that drop
            // out of the world keep their last place in the order below everyone running. After the
            // chequered flag iRacing's official finishing order takes over again.
            bool raceLive = !timedOrder && stName.Length > 0 && sessionState == 4
                && bucket.Exists(d => d.IsConnected && d.LapDistPct >= 0 && d.CurrentLap >= 1);
            if (raceLive)
            {
                bucket.Sort(static (a, b) =>
                {
                    bool va = a.IsConnected && a.LapDistPct >= 0 && a.CurrentLap >= 0;
                    bool vb = b.IsConnected && b.LapDistPct >= 0 && b.CurrentLap >= 0;
                    if (va != vb) return va ? -1 : 1;
                    if (!va)
                    {
                        int ka = a.PositionInClass > 0 ? a.PositionInClass : int.MaxValue;
                        int kb = b.PositionInClass > 0 ? b.PositionInClass : int.MaxValue;
                        int k = ka.CompareTo(kb);
                        return k != 0 ? k : a.CarIdx.CompareTo(b.CarIdx);
                    }
                    double pa = a.CurrentLap + a.LapDistPct, pb = b.CurrentLap + b.LapDistPct;
                    int c = pb.CompareTo(pa);
                    return c != 0 ? c : a.CarIdx.CompareTo(b.CarIdx);
                });
                for (int i = 0; i < bucket.Count; i++)
                    bucket[i] = bucket[i] with { PositionInClass = i + 1 };
            }
            if (timedOrder)
            {
                bucket.Sort(static (a, b) =>
                {
                    float ba = a.BestLapTime > 0 ? a.BestLapTime : float.MaxValue;
                    float bb = b.BestLapTime > 0 ? b.BestLapTime : float.MaxValue;
                    int c = ba.CompareTo(bb);
                    if (c != 0) return c;
                    int na = int.TryParse(a.CarNumber, out var x) ? x : int.MaxValue;
                    int nb = int.TryParse(b.CarNumber, out var y) ? y : int.MaxValue;
                    c = na.CompareTo(nb);
                    return c != 0 ? c : a.CarIdx.CompareTo(b.CarIdx);
                });
                int rank = 0;
                for (int i = 0; i < bucket.Count; i++)
                    bucket[i] = bucket[i] with { PositionInClass = bucket[i].BestLapTime > 0 ? ++rank : 0 };
            }
            // Interval (gap to the car directly ahead in THIS order - the tower order) and
            // gap to the class leader, from iRacing's timing; see ClassGaps (2026-10-24).
            ClassGaps(bucket, timedOrder, s, PaceFor);

            // Laps down PER CLASS: a car shows "+1L" only when it is a genuine
            // full lap behind its OWN class leader (track-distance progress,
            // floored). Reference is the first connected car in class order (the
            // class leader), NOT the overall/GTP leader — otherwise a GT3 car on
            // the lead lap of GT3 gets falsely tagged just because the faster
            // GTP class is a lap ahead.
            double? classLeaderProg = null;
            foreach (var cd in bucket)
            {
                if (cd.IsConnected
                    && LiveGap.Progress(cd.CurrentLap, cd.LapDistPct) is double lp)
                {
                    classLeaderProg = lp;
                    break;
                }
            }
            if (classLeaderProg is double clp)
            {
                for (int i = 0; i < bucket.Count; i++)
                {
                    var cd = bucket[i];
                    if (LiveGap.Progress(cd.CurrentLap, cd.LapDistPct) is double dp
                        && clp > dp)
                    {
                        int ld = (int)Math.Floor(clp - dp);
                        if (ld != cd.LapDifference)
                            bucket[i] = cd with { LapDifference = ld };
                    }
                }
            }
            classes.Add(new DriverClass(className, classColors[className], bucket));
        }

        // The Player/Leader/Camera references captured in the build loop above
        // point at PRE-class-fill Driver copies — IntervalSeconds + LapDifference
        // are still null/0, because the per-class pass rebuilt each bucket entry
        // with `with` (records are immutable, so the originals never got the
        // fill). Re-resolve CameraTarget from the finalized buckets so the
        // featured card can read its interval-to-car-ahead.
        // 2026-10-18 (roadmap item 1): Player and Leader are now re-resolved the
        // same way. Before this, snap.Player.IntervalSeconds was always null, so
        // BattleDetection's gap-to-car-AHEAD was always null and a battle with
        // the car in front could never trigger (only a car closing from behind).
        int playerCarIdx = player?.CarIdx ?? -1, leaderIdx = leader?.CarIdx ?? -1;
        foreach (var cls in classes)
        {
            foreach (var fin in cls.Drivers)
            {
                if (fin.CarIdx == camCarIdx && camCarIdx >= 0) camera = fin;
                if (fin.CarIdx == playerCarIdx && playerCarIdx >= 0) player = fin;
                if (fin.CarIdx == leaderIdx && leaderIdx >= 0) leader = fin;
            }
        }

        var phase = RacePhaseMap.From(sessionState, s.SessionTypeName);
        var info = TrackNames.ResolveInfo(s.TrackRawName);
        // 2026-10-19: laps + length of the LIVE session (the YAML's CurrentSessionNum can lag)
        var totalLaps = s.SessionLapsByNum.TryGetValue(liveSessionNum, out var lapsNow) ? lapsNow : s.TotalLaps;
        double sessionLen = s.SessionLengthByNum.TryGetValue(liveSessionNum, out var lenNow) ? lenNow : 0;
        // 2026-10-27 (#405, Angelo: "I see 2-3 laps after the timer runs out"): in a timed race iRacing's
        // SessionTimeRemain counts from the start of the race SESSION (grid + formation), but the race clock starts at
        // the green (Le Mans 10-27: 2700 s race, green at 261 s, flag at 3010 s). From the green on, time left =
        // length - (SessionTime - SessionTime at the green). The green time is kept per session (and on disk, so a
        // restart mid-race keeps it).
        if (d.SessionTime != null && sessionLen > 0 && sessionLen < 172800
            && (s.SessionTypeName ?? string.Empty).Contains("race", StringComparison.OrdinalIgnoreCase))
        {
            double stNow = data.GetDouble(d.SessionTime);
            string gk = KeyFor(liveSessionNum);
            if (sessionState is >= 1 and <= 3) { greenSeenKey = gk; greenPending = true; }
            else if (sessionState == 4 && greenPending && greenSeenKey == gk) { greenPending = false; GreenTimes.Save(gk, stNow); }
            if (sessionState >= 4 && GreenTimes.Get(gk) is double g0 && stNow >= g0)
                sessionTimeRemain = Math.Max(0, sessionLen - (stNow - g0));
        }
        // overtime: iRacing's remaining time stops at zero (or goes negative); the session's own elapsed clock keeps going
        double overtime = 0;
        if (sessionLen > 0 && sessionLen < 172800 && sessionTimeRemain <= 0.5)
        {
            if (sessionTimeRemain < 0) overtime = -sessionTimeRemain;
            if (d.SessionTime != null)
            {
                double el = data.GetDouble(d.SessionTime);
                if (GreenTimes.Get(KeyFor(liveSessionNum)) is double gz && el >= gz) el -= gz;   // #405: the race clock runs from the green
                if (el > sessionLen) overtime = Math.Max(overtime, el - sessionLen);
            }
        }
        var gridClass = new Dictionary<int, int>();
        foreach (var kv in s.Grid) gridClass[kv.Key] = kv.Value.ClassPos;
        // the race lap is the LEADER's lap ("Lap" is only our own car: wrong when lapped, 0 when spectating)
        if (leaderCarIdx >= 0 && leaderCarIdx < lapBuf.Length && lapBuf[leaderCarIdx] > 0)
            currentLap = lapBuf[leaderCarIdx];
        var lapsRemaining = totalLaps > 0 ? Math.Max(0, totalLaps - currentLap) : 0;

        OnboardData obCam;
        return new RaceSnapshot(
            IsConnected: true,
            CapturedAt: DateTime.UtcNow,
            SessionState: sessionState,
            SessionTypeName: s.SessionTypeName,
            Phase: phase,
            Flags: FlagState.FromBitField(sessionFlagsBits),
            CurrentLap: currentLap,
            TotalLaps: totalLaps,
            LapsRemaining: lapsRemaining,
            SessionTimeRemaining: sessionTimeRemain,
            SessionTimeOfDay: sessionTimeOfDay,
            AirTempC: airTemp,
            TrackTempC: trackTemp,
            TemperatureUnit: d.DisplayUnits != null && SafeInt(data, d.DisplayUnits) == 0 ? "F" : "C",   // iRacing: 0 = imperial
            IsRaining: s.IsRaining,
            TrackRawName: s.TrackRawName,
            TrackDisplayName: s.TrackDisplayName,
            TrackBroadcastName: info?.Name ?? TrackNames.Resolve(s.TrackRawName),
            TrackCountryCode: info?.CountryCode,
            SeriesId: s.SeriesId,
            OpponentsCount: raw.Count,
            PlayerCarIdx: s.PlayerCarIdx,
            CameraCarIdx: camCarIdx,
            Player: player,
            Leader: leader,
            CameraTarget: camera,
            ClassLeaderboard: classes)
        { Extras = BuildExtras(data, d, s), TrackConfigName = s.TrackConfigName, TrackLatitude = s.TrackLat, TrackLongitude = s.TrackLon,
          SessionNum = liveSessionNum, SessionKey = KeyFor(liveSessionNum), SessionLengthSeconds = sessionLen, SessionOvertimeSeconds = overtime, GridClassPositions = gridClass,
          Onboard = obCam = BuildOnboard(data, d, s, camCarIdx),
          // 2026-10-25 (Angelo: the onboard view should have the inputs and sectors "populated already, not just when
          // the camera is on it"): our own car's inputs every frame, whatever the camera shows
          PlayerOnboard = camCarIdx == s.PlayerCarIdx ? obCam : BuildOnboard(data, d, s, s.PlayerCarIdx),
          CarSteer = ReadF(data, d.CarIdxSteer), CarGear = ReadI(data, d.CarIdxGear), CarRpm = ReadF(data, d.CarIdxRPM),
          ResultsOfficial = s.OfficialByNum.TryGetValue(liveSessionNum, out var off) && off,
          SolarAltitude = ReadD(data, d.SolarAltitude), SolarAzimuth = ReadD(data, d.SolarAzimuth), PlayerYawNorth = ReadD(data, d.YawNorth),
          TrackNorthOffset = s.TrackNorthOffset };
    }

    /// <summary>
    /// Fills <c>IntervalSeconds</c> (gap to the car directly ahead in <paramref name="bucket"/>'s
    /// order, which is the tower order) and <c>GapToLeaderSeconds</c> (gap to the class leader =
    /// bucket[0]) for one class, from iRacing's own timing (2026-10-24, DECISIONS.md).
    /// Races: CarIdxF2Time (official time behind the leader), so interval = F2(car) - F2(car ahead)
    /// and class gap = F2(car) - F2(class leader). When F2 is missing (0 / -1: before the green, a car
    /// not yet timed), says the pair is the other way round (the live track-position order moved
    /// before iRacing's timing caught up) or we're in a replay, the pair falls back to CarIdxEstTime
    /// (laps x class lap time + est-time difference), then to the old lap-distance x pace estimate.
    /// Practice / qualifying: differences of fastest laps (F2, else CarIdxBestLapTime).
    /// The class gap is the running sum of intervals so it never disagrees with the order.
    /// Runs once per telemetry update inside BuildSnapshot - overlays only read the results.
    /// </summary>
    void ClassGaps(List<Driver> bucket, bool timedOrder, SessionCache s, Func<float, float, float?> paceFor)
    {
        if (bucket.Count == 0) return;
        bool replay = IsReplay;

        float F2(Driver d) => d.CarIdx >= 0 && d.CarIdx < f2TimeBuf.Length ? f2TimeBuf[d.CarIdx] : -1f;
        float Est(Driver d) => d.CarIdx >= 0 && d.CarIdx < estTimeBuf.Length ? estTimeBuf[d.CarIdx] : -1f;
        float ClassLap(Driver d) => s.Drivers.TryGetValue(d.CarIdx, out var st) && st.ClassEstLap > 0 ? st.ClassEstLap : 0f;
        // Race F2 is "time behind the overall leader": 0 is only real for the overall leader.
        bool RaceF2Ok(Driver d) { float f = F2(d); return !replay && (f > 0f || (f == 0f && d.Position == 1)); }
        bool OnRoad(Driver d) => d.IsConnected && LiveGap.Progress(d.CurrentLap, d.LapDistPct) is double;
        float Best(Driver d) { float f = F2(d); return !replay && f > 0f ? f : d.BestLapTime; }

        // On-road estimate between two cars (no iRacing gap available).
        double? Estimate(Driver ahead, Driver behind)
        {
            if (LiveGap.Progress(ahead.CurrentLap, ahead.LapDistPct) is not double pa
                || LiveGap.Progress(behind.CurrentLap, behind.LapDistPct) is not double pb) return null;
            // keep the pair in road order so the tiny side-by-side gap stays positive
            if (pb > pa) (ahead, behind, pa, pb) = (behind, ahead, pb, pa);
            float lapT = ClassLap(behind);
            if (lapT <= 0) lapT = paceFor(behind.BestLapTime, behind.LastLapTime) ?? 0f;
            float ea = Est(ahead), eb = Est(behind);
            if (lapT > 0 && ea >= 0 && eb >= 0)
            {
                double g = (ahead.CurrentLap - behind.CurrentLap) * (double)lapT + (ea - eb);
                if (g > 0) return g;
            }
            return LiveGap.Between(pa, pb,
                bestLapBehind: paceFor(behind.BestLapTime, behind.LastLapTime),
                bestLapAhead: paceFor(ahead.BestLapTime, ahead.LastLapTime));
        }

        double? Pair(Driver ahead, Driver behind)
        {
            if (timedOrder)
            {
                float ba = Best(ahead), bb = Best(behind);
                return ba > 0 && bb > 0 ? Math.Max(0.0, bb - ba) : null;
            }
            if (RaceF2Ok(ahead) && RaceF2Ok(behind))
            {
                double g = F2(behind) - F2(ahead);
                if (g > 0) return g;
            }
            return Estimate(ahead, behind);
        }

        bool Usable(Driver d) => timedOrder ? Best(d) > 0 : (RaceF2Ok(d) || OnRoad(d));

        var leader = bucket[0];
        bool leaderUsable = Usable(leader);
        bucket[0] = leader with { IntervalSeconds = 0, GapToLeaderSeconds = leaderUsable ? 0 : null };
        double? running = leaderUsable ? 0 : null;
        int aheadIdx = leaderUsable ? 0 : -1, leadIdx = aheadIdx;
        for (int i = 1; i < bucket.Count; i++)
        {
            var cur = bucket[i];
            if (!Usable(cur))
            {
                bucket[i] = cur with { IntervalSeconds = null, GapToLeaderSeconds = null };
                continue;
            }
            if (aheadIdx < 0)
            {
                // nobody usable ahead: this car is the effective class leader
                bucket[i] = cur with { IntervalSeconds = 0, GapToLeaderSeconds = 0 };
                running = 0; aheadIdx = i; leadIdx = i;
                continue;
            }
            // aheadIdx = nearest USABLE car ahead (disconnected / untimed cars are skipped)
            var interval = Pair(bucket[aheadIdx], cur);
            // class gap = running sum of intervals (equals F2(car) - F2(leader) when every
            // interval came from F2); if the chain broke, measure to the leader directly
            double? toLeader = running is double r && interval is double iv ? r + iv : Pair(bucket[leadIdx], cur);
            bucket[i] = cur with { IntervalSeconds = interval, GapToLeaderSeconds = toLeader };
            running = toLeader;
            aheadIdx = i;
        }
    }

    void Publish(RaceSnapshot snap)
    {
        Current = snap;
        SnapshotUpdated?.Invoke(snap);
    }

    // ----- helpers -----

    /// <summary>"7200.0000 sec" / 7200 -> 7200; "unlimited" or the 604800 s placeholder -> 0.</summary>
    static double ParseSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"[0-9]+(\.[0-9]+)?");
        if (!m.Success) return 0;
        if (!double.TryParse(m.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return 0;
        return v > 0 && v < 86400 * 2 ? v : 0;
    }

    static int ParseTotalLaps(IRacingSdkSessionInfo.SessionInfoModel? info, int currentSessionNum)
    {
        if (info?.Sessions == null) return 0;
        foreach (var s in info.Sessions)
        {
            if (s.SessionNum != currentSessionNum) continue;
            // SessionLaps is "unlimited" for timed races, otherwise a number.
            if (int.TryParse(s.SessionLaps, out var n)) return n;
            return 0;
        }
        return 0;
    }

    // ---------------------------------------------------------------- onboard overlay (2026-10-24)
    // 2026-10-24 (Angelo): NOT "cockpit" - that's the view he drives in, so the onboard graphics never appear while
    // he's racing; replays use the gyro / roll bar / nose / gearbox / suspension / wing cameras.
    static readonly string[] OnboardWords = { "roll bar", "rollbar", "gyro", "nose", "gearbox", "susp", "wing", "onboard" };
    static float SafeF(Func<float> f) { try { return f(); } catch { return 0f; } }
    static double ReadD(IRacingSdkData data, IRacingSdkDatum? x)
    {
        if (x == null) return double.NaN;
        try { return data.GetFloat(x); } catch { return double.NaN; }
    }

    static float[] ReadF(IRacingSdkData data, IRacingSdkDatum? x)
    {
        if (x == null) return Array.Empty<float>();
        try { var a = new float[IRacingSdkConst.MaxNumCars]; data.GetFloatArray(x, a, 0, a.Length); return a; } catch { return Array.Empty<float>(); }
    }
    static int[] ReadI(IRacingSdkData data, IRacingSdkDatum? x)
    {
        if (x == null) return Array.Empty<int>();
        try { var a = new int[IRacingSdkConst.MaxNumCars]; data.GetIntArray(x, a, 0, a.Length); return a; } catch { return Array.Empty<int>(); }
    }
    static Dictionary<(int, int), string> ReadCamNames(IRacingSdkSessionInfo info)
    {
        var map = new Dictionary<(int, int), string>();
        try
        {
            foreach (var g in info.CameraInfo?.Groups ?? new())
                if (g?.Cameras != null)
                    foreach (var c in g.Cameras) if (c != null) map[(g.GroupNum, c.CameraNum)] = c.CameraName ?? "";
        }
        catch { }
        return map;
    }
    static Dictionary<int, string> ReadCamGroups(IRacingSdkSessionInfo info)
    {
        var map = new Dictionary<int, string>();
        try { foreach (var g in info.CameraInfo?.Groups ?? new()) if (g != null) map[g.GroupNum] = g.GroupName ?? ""; } catch { }
        return map;
    }

    // speed of a car other than ours, from how fast it moves round the lap (smoothed)
    int obCar = -1; float obPct = -1f; double obT = -1; float obSpeed;
    readonly float[] obF = new float[IRacingSdkConst.MaxNumCars]; readonly int[] obI = new int[IRacingSdkConst.MaxNumCars];
    double lastPedalsSeenT = -1e9;
    // lockup estimate (2026-10-25, Angelo: the GTP has no ABS - show a lockup light instead). iRacing sends no wheel
    // speeds, so a lock is read from the car slowing LESS than it normally does for that much brake at that speed:
    // the best braking seen per 10 m/s band is learned as we go, and a lock is a sustained drop well below it.
    readonly float[] brakeGrip = new float[12];
    double lockSinceT = -1;
    // 2026-10-25: TC from the car's pull when the pedal isn't recorded (replays): best acceleration seen per speed band on
    // full throttle, and when the gear last changed (a shift dips the pull too)
    readonly float[] driveGrip = new float[12];
    bool rawThrottleSeen; int lastGearSeen = -99; double gearChangeT = -1, tcSinceT = -1;

    OnboardData BuildOnboard(IRacingSdkData data, DatumCache d, SessionCache s, int camCar)
    {
        try
        {
            float F(IRacingSdkDatum? x) { try { return x == null ? 0f : data.GetFloat(x); } catch { return 0f; } }
            int I(IRacingSdkDatum? x, int fb = -1) { try { return x == null ? fb : data.GetInt(x); } catch { return fb; } }
            int grp = I(d.CamGroupNumber), cam = I(d.CamCameraNumber);
            string gname = grp >= 0 && s.CamGroups.TryGetValue(grp, out var gn) ? gn : "";
            // 2026-10-24 (Angelo: TV1 "runs through a bunch of camera angles"): a TV group cuts between trackside and
            // in-car cameras, so the live CAMERA's name decides ("CamGyro", "CamRoll Bar"...), else the group's.
            string cname = grp >= 0 && cam >= 0 && s.CamNames.TryGetValue((grp, cam), out var cn) ? cn : "";
            string gl = (cname.Length > 0 ? cname : gname).ToLowerInvariant();
            bool onboard = false;
            if (camCar >= 0 && gl.Length > 0 && !gl.Contains("chopper", StringComparison.Ordinal) && !gl.Contains("blimp", StringComparison.Ordinal))
                foreach (var w in OnboardWords) if (gl.Contains(w, StringComparison.Ordinal)) { onboard = true; break; }
            string groupOnly = gname;   // 2026-10-26 (#234): kept for the lens rain's TV / aerial test
            if (cname.Length > 0) gname = cname;
            bool mine = camCar >= 0 && camCar == s.PlayerCarIdx;
            bool replay = d.IsReplayPlaying != null && data.GetBool(d.IsReplayPlaying);
            double t = d.SessionTimeD == null ? 0 : data.GetDouble(d.SessionTimeD);
            float thr = 0, brk = 0, clu = 0, steer = 0, steerMax = 0, speed = 0, rpm = 0; int gear = 0;
            if (mine)
            {
                thr = F(d.Throttle); brk = F(d.Brake); clu = 1f - F(d.Clutch);   // iRacing: Clutch 1 = released
                steer = F(d.SteeringWheelAngle); steerMax = F(d.SteeringWheelAngleMax);
                speed = F(d.Speed); gear = I(d.Gear, 0); rpm = F(d.RPM);
                if (thr > 0.02f || brk > 0.02f || speed > 3f) lastPedalsSeenT = t;
            }
            if (!mine || speed <= 0.01f)
            {
                // steering / gear / RPM for any car; speed from lap progress
                if (camCar >= 0)
                {
                    if (d.CarIdxSteer != null) { data.GetFloatArray(d.CarIdxSteer, obF, 0, obF.Length); if (camCar < obF.Length) steer = obF[camCar]; }
                    if (d.CarIdxGear != null) { data.GetIntArray(d.CarIdxGear, obI, 0, obI.Length); if (camCar < obI.Length) gear = obI[camCar]; }
                    if (d.CarIdxRPM != null) { data.GetFloatArray(d.CarIdxRPM, obF, 0, obF.Length); if (camCar < obF.Length) rpm = obF[camCar]; }
                    float pct = camCar < lapDistPctBuf.Length ? lapDistPctBuf[camCar] : -1f;
                    double len = ParseLengthM(s.TrackLength);
                    if (camCar != obCar || pct < 0 || obPct < 0 || t <= obT) { obCar = camCar; obPct = pct; obT = t; obSpeed = 0; }
                    else if (t - obT >= 0.1)
                    {
                        float dp = pct - obPct; if (dp < -0.5f) dp += 1f; if (dp > 0.5f) dp -= 1f;
                        float v = dp > 0 && len > 0 ? (float)(dp * len / (t - obT)) : 0f;
                        obSpeed += (v - obSpeed) * 0.35f;
                        obPct = pct; obT = t;
                    }
                    speed = obSpeed;
                }
            }
            bool pedals = mine && (!replay || t - lastPedalsSeenT < 4.0);
            bool metric = I(d.DisplayUnits, 1) != 0;
            float lapPct = camCar >= 0 && camCar < lapDistPctBuf.Length ? lapDistPctBuf[camCar] : -1f;
            float delta = 0f; bool deltaOk = false;
            if (mine)
            {
                if (d.LapDistPct != null) { float lp = F(d.LapDistPct); if (lp >= 0) lapPct = lp; }
                delta = F(d.LapDeltaToBestLap);
                try { deltaOk = d.LapDeltaToBestLapOk != null && data.GetBool(d.LapDeltaToBestLapOk); } catch { deltaOk = false; }
            }
            float lat = mine ? F(d.LatAccel) : 0f, lng = mine ? F(d.LongAccel) : 0f;
            bool hasG = pedals && d.LatAccel != null && d.LongAccel != null;
            // TC / ABS (2026-10-25): ABS straight from iRacing; traction control shows as the throttle the engine gets
            // (Throttle) falling short of the pedal (ThrottleRaw) - not while the pit limiter is on
            bool abs = false, tc = false;
            bool carHasAbs = d.DcABS != null;
            bool hasAids = pedals && (d.BrakeABSactive != null || d.ThrottleRaw != null);
            if (hasAids)
            {
                // 2026-10-25 (Angelo: "it isn't showing lockups even though the lights on the dash show it", "instead of ABS
                // it should say LOCK"): one LOCK light for every car - lit while ABS works (cars that really have it) or
                // while braking grip suddenly drops (a locked wheel). The GTP lists an ABS setting (always 0) but has no ABS,
                // so the grip estimate runs for every car.
                try { abs = d.BrakeABSactive != null && data.GetBool(d.BrakeABSactive) && brk > 0.05f; } catch { abs = false; }
                {
                    float decel = -F(d.LongAccel);
                    int band = Math.Clamp((int)(speed / 10f), 0, brakeGrip.Length - 1);
                    bool braking = brk > 0.35f && speed > 15f && d.LongAccel != null;
                    if (braking)
                    {
                        float grip = decel / brk;
                        if (grip > brakeGrip[band]) brakeGrip[band] = grip;
                        bool low = brakeGrip[band] > 8f && grip < brakeGrip[band] * 0.7f;
                        if (low) { if (lockSinceT < 0) lockSinceT = t; } else lockSinceT = -1;
                        abs |= lockSinceT >= 0 && t - lockSinceT >= 0.08;
                    }
                    else lockSinceT = -1;
                }
                bool limiter = d.PitLimiterFlags != null && (I(d.PitLimiterFlags, 0) & 0x10) != 0;
                float raw = d.ThrottleRaw != null ? F(d.ThrottleRaw) : 0f;
                if (raw > 0.02f) rawThrottleSeen = true;
                if (gear != lastGearSeen) { lastGearSeen = gear; gearChangeT = t; }
                if (rawThrottleSeen)
                    // live: traction control = the engine getting less throttle than the pedal asks for
                    tc = !limiter && raw > 0.25f && raw - thr > 0.06f && speed > 8f;
                else if (d.LongAccel != null && !limiter)
                {
                    // replays (no pedal recorded): flat out but pulling clearly less than this car can at this speed,
                    // not just after a gear change - the rear is spinning and TC is cutting
                    float pull = F(d.LongAccel);
                    int band = Math.Clamp((int)(speed / 10f), 0, driveGrip.Length - 1);
                    bool flat = thr > 0.9f && speed > 8f && speed < 70f && t - gearChangeT > 0.35;
                    if (flat && pull > driveGrip[band]) driveGrip[band] = pull;
                    bool cut = flat && driveGrip[band] > 3f && pull < driveGrip[band] * 0.72f;
                    if (cut) { if (tcSinceT < 0) tcSinceT = t; } else tcSinceT = -1;
                    tc = tcSinceT >= 0 && t - tcSinceT >= 0.1;
                }
            }
            // 2026-10-29 (car parity): the shift lights / redline belong to the car on camera. iRacing gives them for our
            // own car only (DriverCarSLShiftRPM / DriverCarRedLine) - right for us and for any car of the same model;
            // another model uses its Lovely Car Data shift rpm for the gear it's in (CarCatalog), and a car with
            // no sourced value gets none (0: the tach shows no shift lights rather than a made-up number).
            float shiftRpm = s.ShiftRpm, redRpm = s.RedlineRpm;
            CarCatalog.CarInfo? camInfo = null;
            if (camCar >= 0 && s.Drivers.TryGetValue(camCar, out var camStat))
            {
                camInfo = CarCatalog.Find(camStat.CarId, camStat.CarPath);
                s.Drivers.TryGetValue(s.PlayerCarIdx, out var meStat);
                bool sameModel = mine || (camStat.CarPath.Length > 0 && string.Equals(camStat.CarPath, meStat.CarPath, StringComparison.OrdinalIgnoreCase));
                if (!sameModel) { shiftRpm = camInfo?.ShiftFor(gear) ?? 0f; redRpm = 0f; }
            }
            else if (!mine) { shiftRpm = 0f; redRpm = 0f; }
            if (shiftRpm <= 0f && camInfo != null) shiftRpm = camInfo.ShiftFor(gear);   // iRacing sent none: the sourced value
            // TC: iRacing's dcTractionControl (or dcTractionControl2) on our car, or a sourced "has TC" for it - and never
            // for a car a source says has none
            bool carHasTc = camInfo?.Tc ?? (d.DcTC != null || d.DcTC2 != null);
            return new OnboardData(camCar, grp, gname, onboard, mine, pedals, thr, brk, clu, steer, steerMax, speed, gear, rpm,
                                   shiftRpm, redRpm, metric, lapPct, delta, deltaOk, lat, lng, hasG, abs, tc, hasAids, false, carHasTc)
                   { GroupOnlyName = groupOnly };
        }
        catch { return OnboardData.None; }
    }

    static int SafeInt(IRacingSdkData data, IRacingSdkDatum x) { try { return data.GetInt(x); } catch { return -1; } }

    static double ParseLengthM(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var parts = text.Trim().Split(' ');
        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return 0;
        string unit = parts.Length > 1 ? parts[1].ToLowerInvariant() : "km";
        return unit.StartsWith("mi") ? v * 1609.344 : unit == "m" ? v : v * 1000.0;
    }

    static SessionExtras BuildExtras(IRacingSdkData data, DatumCache d, SessionCache s)
    {
        float F(IRacingSdkDatum? x) { try { return x == null ? 0f : data.GetFloat(x); } catch { return 0f; } }
        int I(IRacingSdkDatum? x) { try { return x == null ? -1 : data.GetInt(x); } catch { return -1; } }
        return new SessionExtras(
            WindMs: F(d.WindVel), WindDirRad: F(d.WindDir),
            Humidity: d.RelativeHumidity == null ? -1f : F(d.RelativeHumidity),
            Skies: I(d.Skies), PrecipNow: F(d.Precipitation), RainChancePct: s.RainChance,
            TrackLength: s.TrackLength, TrackTurns: s.TrackTurns, TrackCity: s.TrackCity, TrackCountry: s.TrackCountry)
        { TrackWetness = Math.Max(0, I(d.TrackWetness)) };
    }

    /// <summary>iRacing writes coordinates like "47.949722 m" - take the leading (signed) number.</summary>
    static double ParseCoord(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return double.NaN;
        var span = text.AsSpan().Trim();
        int i = 0;
        while (i < span.Length && (char.IsDigit(span[i]) || span[i] == '.' || span[i] == '-' || span[i] == '+')) i++;
        return i > 0 && double.TryParse(span[..i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    }

    static float ParsePct(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        var span = text.AsSpan().TrimStart();
        int i = 0;
        while (i < span.Length && (char.IsDigit(span[i]) || span[i] == '.')) i++;
        return i > 0 && float.TryParse(span[..i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;
    }

    static bool ParseIsRaining(string? precipitation)
    {
        if (string.IsNullOrEmpty(precipitation)) return false;
        // iRacing emits "0 %" for none, or a percent string. Anything > 0 ⇒ raining.
        var span = precipitation.AsSpan().TrimStart();
        int i = 0;
        while (i < span.Length && (char.IsDigit(span[i]) || span[i] == '.')) i++;
        return i > 0 && double.TryParse(span[..i], System.Globalization.CultureInfo.InvariantCulture, out var pct) && pct > 0;
    }

    /// <summary>
    /// 2026-10-18: accented driver names arrived garbled ("Á. LÓPEZ" shown as
    /// "Ã. LÃ³PEZ") - UTF-8 bytes decoded as Latin-1. Re-decode ONLY when the
    /// string shows that exact signature (a Ã/Â lead char followed by a
    /// continuation char) and round-trips cleanly; correct names are untouched.
    /// </summary>
    static string FixMojibake(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        // Signature: a UTF-8 lead byte rendered as Ã/Â/Ä/Å (0xC2-0xC5) followed by
        // a continuation byte rendered as a non-ASCII char. Correct names never
        // contain that pair, so they are returned untouched.
        bool suspect = false;
        for (int i = 0; i < s.Length - 1; i++)
        {
            char a = s[i], b = s[i + 1];
            if (a >= '\u00C2' && a <= '\u00C5' && b >= '\u0080') { suspect = true; break; }
        }
        if (!suspect) return s;

        // Undo the bad decode. It was Windows-1252 here (e.g. 0x92 -> U+2019),
        // which plain Latin-1 can't reverse, so try 1252 first, then Latin-1.
        var strictUtf8 = new System.Text.UTF8Encoding(false, true);
        foreach (var enc in MojibakeSourceEncodings())
        {
            try { return strictUtf8.GetString(enc.GetBytes(s)); }   // throws if not valid UTF-8
            catch { /* try the next encoding */ }
        }
        return s;
    }

    /// <summary>True when a name shows UTF-8-read-as-ANSI garbling (a Ã/Â/Ä/Å
    /// lead char followed by another non-ASCII char, or cut off at the end /
    /// before a separator). Clean accented names ("Brösicke", "Gonçalves") never match.</summary>
    static bool LooksGarbled(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        for (int i = 0; i < s.Length; i++)
        {
            char a = s[i];
            if (a < '\u00C2' || a > '\u00C5') continue;
            if (i == s.Length - 1) return true;                       // lead byte cut off at end
            char b = s[i + 1];
            if (b >= '\u0080' || b == ',' || b == ' ') return true;   // mojibake pair / truncated
        }
        return false;
    }

    static System.Text.Encoding[]? mojibakeEncodings;
    static System.Text.Encoding[] MojibakeSourceEncodings()
    {
        if (mojibakeEncodings != null) return mojibakeEncodings;
        var list = new List<System.Text.Encoding>();
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            list.Add(System.Text.Encoding.GetEncoding(1252,
                System.Text.EncoderFallback.ExceptionFallback, System.Text.DecoderFallback.ExceptionFallback));
        }
        catch { /* code pages unavailable - Latin-1 only */ }
        list.Add(System.Text.Encoding.GetEncoding("ISO-8859-1",
            System.Text.EncoderFallback.ExceptionFallback, System.Text.DecoderFallback.ExceptionFallback));
        return mojibakeEncodings = list.ToArray();
    }

    static string CleanCarName(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        // Drop a leading manufacturer token if it's also the brand prefix
        // ("Porsche 911 GT3 R" → "911 GT3 R"). Cheap heuristic; full mapping
        // moves to a CarNames.cs port when the tower (Port 04) needs it.
        return raw.Trim();
    }

    /// <summary>2026-10-29 (car parity): iRacing's own CarScreenName; the catalog's name only when iRacing sends none.</summary>
    static string CarNameFor(string? raw, CarCatalog.CarInfo? car)
    {
        var n = CleanCarName(raw);
        return n.Length == 0 && car != null ? car.Name : n;
    }

    /// <summary>2026-10-29 (car parity): the maker from the car's fact sheet (its logo slug, else the maker as text for
    /// cards that fall back to text), so "HPD", "Global", "Stock Car Brasil", "Supercars", "V8 Supercar", "SCCA"... find
    /// the right logo. Cars outside the catalog keep the first-word guess, now with the same aliases.</summary>
    static string BrandFor(string? raw, CarCatalog.CarInfo? car)
    {
        var guess = ExtractBrand(raw);
        if (car == null) return guess;
        // the old word when it already finds the same logo (keeps "Mercedes-AMG", "McLaren"... as the cards print them)
        if (car.LogoSlug != null && string.Equals(ManufacturerBrand.Resolve(guess), car.LogoSlug, StringComparison.Ordinal)) return guess;
        return (car.LogoSlug ?? car.Maker) is { Length: > 0 } m ? m : guess;
    }

    static string ExtractBrand(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        // Corvettes ("Chevrolet Corvette Z06 GT3.R", "Chevrolet Corvette C8.R GTE")
        // show the Corvette crossflags logo, not Chevrolet's bowtie (Angelo 2026-10-18).
        if (raw.Contains("Corvette", StringComparison.OrdinalIgnoreCase)) return "Corvette";
        // Take the first token before a space (e.g. "Porsche 911 GT3 R" → "Porsche").
        var idx = raw.IndexOf(' ');
        var first = idx > 0 ? raw[..idx] : raw;
        // 2026-10-29 (car parity audit, G1): series names in front of the maker - the maker is the next word
        // ("Supercars Ford Mustang GT", "V8 Supercar Holden ...", "Stock Car Brasil Toyota Corolla"); HPD is Honda's
        // racing arm, Spec Racer Ford is a Ford, the Global MX-5 is a Mazda
        var lead = new[] { "Supercars ", "V8 Supercar ", "Stock Car Brasil ", "Stockcar Brasil " }
            .FirstOrDefault(p => raw.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (lead != null)
        {
            var rest = raw[lead.Length..].TrimStart();
            var sp = rest.IndexOf(' ');
            return sp > 0 ? rest[..sp] : rest;
        }
        return first.ToUpperInvariant() switch
        {
            "HPD" => "Honda",
            "SCCA" => "Ford",
            "GLOBAL" when raw.Contains("Mazda", StringComparison.OrdinalIgnoreCase) => "Mazda",
            _ => first,
        };
    }

    static string NormalizeHex(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var r = raw.Trim();
        if (r.StartsWith('#')) r = r[1..];
        if (r.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) r = r[2..];   // iRacing sends "0xffda59"
        if (r.Length is not (3 or 6 or 8) || !r.All(Uri.IsHexDigit)) return string.Empty;
        if (r.Length == 3) r = string.Concat(r[0], r[0], r[1], r[1], r[2], r[2]);
        return "#" + r;
    }

    readonly record struct DriverStatic(
        string Name,
        string Abbrev,
        string Initials,
        string CarNumber,
        string CarName,
        string CarBrand,
        string ClassColor,
        string LicString,
        string LicColor,
        int IRating,
        bool IsSpectator,
        bool IsPaceCar,
        int Incidents = -1,
        float ClassEstLap = 0f,
        string ClassLabel = "OTHER",
        string CarPath = "",
        int UserId = 0,
        int TeamId = 0,
        string CarDesign = "",
        string NumberDesign = "",
        string Sponsors = "",
        int CarId = 0);

    sealed record SessionCache(
        int PlayerCarIdx,
        Dictionary<int, DriverStatic> Drivers,
        string TrackRawName,
        string TrackDisplayName,
        int TotalLaps,
        string SessionTypeName,
        bool IsRaining,
        int? SeriesId,
        Dictionary<int, (int Pos, int ClassPos)> Grid)
    {
        /// <summary>SessionNum -> SessionType for every session in the event.</summary>
        public Dictionary<int, string> SessionTypes { get; init; } = new();
        public Dictionary<int, int> SessionLapsByNum { get; init; } = new();
        public Dictionary<int, double> SessionLengthByNum { get; init; } = new();
        public Dictionary<int, bool> OfficialByNum { get; init; } = new();
        public Dictionary<int, HashSet<int>> DqByNum { get; init; } = new();

        public float RainChance { get; init; }
        public string TrackLength { get; init; } = "";
        public Dictionary<int, string> CamGroups { get; init; } = new();
        /// <summary>(group, camera) -> camera name: TV groups mix trackside cameras with in-car ones
        /// ("CamGyro", "CamRoll Bar", "CamCockpit"...) and a "CamChopper".</summary>
        public Dictionary<(int, int), string> CamNames { get; init; } = new();
        public float ShiftRpm { get; init; }
        public float RedlineRpm { get; init; }
        public int TrackTurns { get; init; }
        public string TrackCity { get; init; } = "";
    public string TrackConfigName { get; init; } = "";
        public string TrackCountry { get; init; } = "";
        public double TrackLat { get; init; } = double.NaN;
        public double TrackNorthOffset { get; init; } = double.NaN;
        public double TrackLon { get; init; } = double.NaN;

        /// <summary>CarIdx -> qualifying fastest time (s), from QualifyResultsInfo.</summary>
        public Dictionary<int, float> QualTimes { get; init; } = new();

        public static SessionCache Empty { get; } = new(
            -1, new Dictionary<int, DriverStatic>(), "", "", 0, "", false, null,
            new Dictionary<int, (int Pos, int ClassPos)>());
    }

    readonly record struct RawRow(
        int CarIdx,
        DriverStatic Stat,
        int Position,
        int ClassPosition,
        int CurrentLap,
        float LapDistPct,
        int LapsCompleted,
        float BestLap,
        float LastLap,
        bool OnPitRoad,
        int TrackSurface,
        double? Progress);

    sealed class DatumCache(IRacingSdkData d)
    {
        public IRacingSdkDatum CarIdxLap           = d.TelemetryDataProperties["CarIdxLap"];
        public IRacingSdkDatum CarIdxLapCompleted  = d.TelemetryDataProperties["CarIdxLapCompleted"];
        public IRacingSdkDatum CarIdxLapDistPct    = d.TelemetryDataProperties["CarIdxLapDistPct"];
        public IRacingSdkDatum CarIdxBestLapTime   = d.TelemetryDataProperties["CarIdxBestLapTime"];
        public IRacingSdkDatum CarIdxLastLapTime   = d.TelemetryDataProperties["CarIdxLastLapTime"];
        public IRacingSdkDatum CarIdxPosition      = d.TelemetryDataProperties["CarIdxPosition"];
        public IRacingSdkDatum CarIdxClassPosition = d.TelemetryDataProperties["CarIdxClassPosition"];
        public IRacingSdkDatum CarIdxOnPitRoad     = d.TelemetryDataProperties["CarIdxOnPitRoad"];
        public IRacingSdkDatum CarIdxTrackSurface  = d.TelemetryDataProperties["CarIdxTrackSurface"];
        public IRacingSdkDatum SessionState        = d.TelemetryDataProperties["SessionState"];
        public IRacingSdkDatum SessionNum          = d.TelemetryDataProperties["SessionNum"];
        public IRacingSdkDatum SessionFlags        = d.TelemetryDataProperties["SessionFlags"];
        public IRacingSdkDatum CamCarIdx           = d.TelemetryDataProperties["CamCarIdx"];
        public IRacingSdkDatum Lap                 = d.TelemetryDataProperties["Lap"];
        public IRacingSdkDatum SessionTimeRemain   = d.TelemetryDataProperties["SessionTimeRemain"];
        public IRacingSdkDatum SessionTimeOfDay    = d.TelemetryDataProperties["SessionTimeOfDay"];
        public IRacingSdkDatum AirTemp             = d.TelemetryDataProperties["AirTemp"];
        public IRacingSdkDatum TrackTempCrew       = d.TelemetryDataProperties["TrackTempCrew"];
        // optional weather channels (2026-10-18) - absent in some builds, so looked up safely
        public IRacingSdkDatum? WindVel            = Opt(d, "WindVel");
        public IRacingSdkDatum? WindDir            = Opt(d, "WindDir");
        public IRacingSdkDatum? RelativeHumidity   = Opt(d, "RelativeHumidity");
        public IRacingSdkDatum? Skies              = Opt(d, "Skies");
        public IRacingSdkDatum? Precipitation      = Opt(d, "Precipitation");
        public IRacingSdkDatum? TrackWetness       = Opt(d, "TrackWetness");
        public IRacingSdkDatum? PlayerIncidents    = Opt(d, "PlayerCarMyIncidentCount");
        public IRacingSdkDatum? WeatherDeclaredWet = Opt(d, "WeatherDeclaredWet");
        public IRacingSdkDatum? SessionTime        = Opt(d, "SessionTime");
        public IRacingSdkDatum? IsReplayPlaying    = Opt(d, "IsReplayPlaying");
        public IRacingSdkDatum? ReplayPlaySpeed    = Opt(d, "ReplayPlaySpeed");
        public IRacingSdkDatum? ReplayPlaySlowMotion = Opt(d, "ReplayPlaySlowMotion");
        public IRacingSdkDatum? ReplayFrameNumEnd  = Opt(d, "ReplayFrameNumEnd");   // 2026-10-26 (#235)
        public IRacingSdkDatum? ReplayFrameNum     = Opt(d, "ReplayFrameNum");      // 2026-10-29: 0 = no tape position (live)
        public IRacingSdkDatum? IsOnTrack          = Opt(d, "IsOnTrack");           // 2026-10-27 (#338) DrivingState
        public IRacingSdkDatum? IsOnTrackCar       = Opt(d, "IsOnTrackCar");
        // onboard overlay (2026-10-24)
        public IRacingSdkDatum? CamGroupNumber     = Opt(d, "CamGroupNumber");
        public IRacingSdkDatum? CamCameraNumber    = Opt(d, "CamCameraNumber");
        public IRacingSdkDatum? Throttle           = Opt(d, "Throttle");
        public IRacingSdkDatum? Brake              = Opt(d, "Brake");
        public IRacingSdkDatum? Clutch             = Opt(d, "Clutch");
        public IRacingSdkDatum? SteeringWheelAngle = Opt(d, "SteeringWheelAngle");
        public IRacingSdkDatum? SteeringWheelAngleMax = Opt(d, "SteeringWheelAngleMax");
        public IRacingSdkDatum? Speed              = Opt(d, "Speed");
        public IRacingSdkDatum? Gear               = Opt(d, "Gear");
        public IRacingSdkDatum? RPM                = Opt(d, "RPM");
        public IRacingSdkDatum? CarIdxSteer        = Opt(d, "CarIdxSteer");
        // 2026-10-27 (W11): the sim's sun and our heading
        public IRacingSdkDatum? SolarAltitude      = Opt(d, "SolarAltitude");
        public IRacingSdkDatum? SolarAzimuth       = Opt(d, "SolarAzimuth");
        public IRacingSdkDatum? YawNorth           = Opt(d, "YawNorth");
        public IRacingSdkDatum? CarIdxGear         = Opt(d, "CarIdxGear");
        public IRacingSdkDatum? CarIdxRPM          = Opt(d, "CarIdxRPM");
        public IRacingSdkDatum? DisplayUnits       = Opt(d, "DisplayUnits");
        public IRacingSdkDatum? LapDistPct         = Opt(d, "LapDistPct");
        public IRacingSdkDatum? LatAccel           = Opt(d, "LatAccel");
        public IRacingSdkDatum? ThrottleRaw        = Opt(d, "ThrottleRaw");
        public IRacingSdkDatum? BrakeABSactive     = Opt(d, "BrakeABSactive");
        public IRacingSdkDatum? DcABS              = Opt(d, "dcABS");               // only cars that HAVE ABS send this
        public IRacingSdkDatum? DcTC               = Opt(d, "dcTractionControl");
        public IRacingSdkDatum? DcTC2              = Opt(d, "dcTractionControl2");   // 2026-10-29: cars with only the second TC dial
        public IRacingSdkDatum? PitLimiterFlags    = Opt(d, "EngineWarnings");
        public IRacingSdkDatum? LongAccel          = Opt(d, "LongAccel");
        public IRacingSdkDatum? LapDeltaToBestLap  = Opt(d, "LapDeltaToBestLap");
        public IRacingSdkDatum? LapDeltaToBestLapOk = Opt(d, "LapDeltaToBestLap_OK");
        public IRacingSdkDatum? SessionTimeD       = Opt(d, "SessionTime");
        public IRacingSdkDatum? CarIdxF2Time       = Opt(d, "CarIdxF2Time");
        public IRacingSdkDatum? CarIdxEstTime      = Opt(d, "CarIdxEstTime");
        static IRacingSdkDatum? Opt(IRacingSdkData d, string n) => d.TelemetryDataProperties.TryGetValue(n, out var x) ? x : null;

        public static DatumCache Resolve(IRacingSdkData d) => new(d);
    }
}

/// <summary>2026-10-27 (#405): SessionTime at the green flag of each race session (key = RaceStateService.KeyFor),
/// kept on disk (Documents\iRacing\Grandstand\green_times.json) so a restart mid-race still knows it.</summary>
internal static class GreenTimes
{
    static readonly object Gate = new();
    static Dictionary<string, double>? map;
    static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "green_times.json");

    static Dictionary<string, double> Map()
    {
        if (map != null) return map;
        try { map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(FilePath)); } catch { }
        return map ??= new Dictionary<string, double>();
    }

    public static double? Get(string key) { lock (Gate) return Map().TryGetValue(key, out var v) ? v : null; }

    public static void Save(string key, double sessionTime)
    {
        lock (Gate)
        {
            var m = Map();
            m[key] = sessionTime;
            if (m.Count > 60) foreach (var k in m.Keys.Take(m.Count - 60).ToList()) m.Remove(k);
            var copy = new Dictionary<string, double>(m);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); lock (Gate) File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(copy)); } catch { }
            });
        }
    }
}
