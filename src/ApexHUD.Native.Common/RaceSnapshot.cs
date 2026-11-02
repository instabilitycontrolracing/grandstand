namespace ApexHUD.Native.Common;

/// <summary>
/// Immutable, point-in-time snapshot of the race state. Published by
/// <c>RaceStateService</c> on every telemetry tick; consumers read the snapshot
/// they're holding and re-paint on their own render loop. Mirroring the legacy
/// <c>NewData.*</c> shapes consolidated into one record so overlays can bind
/// without HTTP or JSON.
/// </summary>
/// <param name="IsConnected">True only while iRacing is actively broadcasting.</param>
/// <param name="SessionTimeRemaining">Seconds left in the current session (timed races); 0 / negative for lap-limited.</param>
/// <param name="SessionTimeOfDay">Sim wall clock in seconds since midnight (sun position).</param>
/// <param name="ClassLeaderboard">Per-class ordered driver groups, top class first (lowest leader position).</param>
/// <param name="SeriesId">iRacing <c>WeekendInfo.SeriesID</c> (null when not populated). Drives the grid eyebrow via <see cref="SeriesNames"/>.</param>
public sealed record RaceSnapshot(
    bool IsConnected,
    DateTime CapturedAt,
    int? SessionState,
    string SessionTypeName,
    RacePhase Phase,
    FlagState Flags,
    int CurrentLap,
    int TotalLaps,
    int LapsRemaining,
    double SessionTimeRemaining,
    double SessionTimeOfDay,
    float AirTempC,
    float TrackTempC,
    string TemperatureUnit,
    bool IsRaining,
    string TrackRawName,
    string TrackDisplayName,
    string TrackBroadcastName,
    string? TrackCountryCode,
    int? SeriesId,
    int OpponentsCount,
    int PlayerCarIdx,
    int CameraCarIdx,
    Driver? Player,
    Driver? Leader,
    Driver? CameraTarget,
    IReadOnlyList<DriverClass> ClassLeaderboard)
{
    /// <summary>2026-10-18: live weather + track facts for the pre-race info cards.</summary>
    public SessionExtras Extras { get; init; } = SessionExtras.Empty;
    /// <summary>Track layout, e.g. "Full Course" (picks the right track map).</summary>
    public string TrackConfigName { get; init; } = "";
    /// <summary>2026-10-19: the live session's number (practice / qualify / race ...).</summary>
    public int SessionNum { get; init; } = -1;
    /// <summary>Which race this is ("subsession:session:track") - files kept on disk (race diary, sectors) are
    /// matched against it, so a Grandstand restart picks up where it left off.</summary>
    public string SessionKey { get; init; } = "";
    /// <summary>2026-10-19: official length of the live session in seconds, from iRacing's
    /// session info (0 = unknown or unlimited). Drives the session-bar progress.</summary>
    public double SessionLengthSeconds { get; init; }
    /// <summary>2026-10-24: the camera and the camera car's inputs, for the onboard overlay.</summary>
    public OnboardData Onboard { get; init; } = OnboardData.None;
    /// <summary>Our own car's inputs (pedals, g, lap delta) every frame, whatever the camera is on (2026-10-25).</summary>
    public OnboardData PlayerOnboard { get; init; } = OnboardData.None;
    /// <summary>2026-10-25 (pack battle card): every car's steering (rad, + left), gear and RPM, by CarIdx.
    /// iRacing sends these for all cars; pedals only for the player (see <see cref="Onboard"/>).</summary>
    public float[] CarSteer { get; init; } = Array.Empty<float>();
    public int[] CarGear { get; init; } = Array.Empty<int>();
    public float[] CarRpm { get; init; } = Array.Empty<float>();
    /// <summary>2026-10-24 (Angelo): how far past zero a timed session's clock is, in seconds (0 while time
    /// remains). Endurance broadcasts keep the clock running as -0:42 after it expires.</summary>
    public double SessionOvertimeSeconds { get; init; }
    /// <summary>2026-10-19: CarIdx -> starting position in class (from qualifying), for the
    /// race story (places gained / lost since the start).</summary>
    public IReadOnlyDictionary<int, int> GridClassPositions { get; init; } = new Dictionary<int, int>();
    /// <summary>2026-10-20: the circuit's real-world position (degrees; NaN = unknown), from iRacing's
    /// WeekendInfo - the globe intro flies here.</summary>
    public double TrackLatitude { get; init; } = double.NaN;
    /// <summary>2026-10-20: iRacing has made this session's results official (post-race outro
    /// switches PROVISIONAL -> OFFICIAL).</summary>
    public bool ResultsOfficial { get; init; }
    public double TrackLongitude { get; init; } = double.NaN;
    /// <summary>2026-10-27 (W11 lens alignment): the sim's own sun - SolarAltitude / SolarAzimuth telemetry, radians
    /// (azimuth clockwise from north). NaN when the sim doesn't send them.</summary>
    public double SolarAltitude { get; init; } = double.NaN;
    public double SolarAzimuth { get; init; } = double.NaN;
    /// <summary>2026-10-27: the player's car heading, clockwise from true north (YawNorth, radians; NaN = unknown).
    /// Checked against the GPS trace of a Le Mans .ibt: 0.0 deg mean, 0.7 deg spread.</summary>
    public double PlayerYawNorth { get; init; } = double.NaN;
    /// <summary>2026-10-27: WeekendInfo.TrackNorthOffset (radians): YawNorth = TrackNorthOffset - Yaw.</summary>
    public double TrackNorthOffset { get; init; } = double.NaN;

    public static readonly RaceSnapshot Disconnected = new(
        IsConnected: false,
        CapturedAt: DateTime.UtcNow,
        SessionState: null,
        SessionTypeName: string.Empty,
        Phase: RacePhase.Unknown,
        Flags: FlagState.Empty,
        CurrentLap: 0,
        TotalLaps: 0,
        LapsRemaining: 0,
        SessionTimeRemaining: 0,
        SessionTimeOfDay: 0,
        AirTempC: 0,
        TrackTempC: 0,
        TemperatureUnit: "C",
        IsRaining: false,
        TrackRawName: string.Empty,
        TrackDisplayName: string.Empty,
        TrackBroadcastName: string.Empty,
        TrackCountryCode: null,
        SeriesId: null,
        OpponentsCount: 0,
        PlayerCarIdx: -1,
        CameraCarIdx: -1,
        Player: null,
        Leader: null,
        CameraTarget: null,
        ClassLeaderboard: Array.Empty<DriverClass>());
}

/// <summary>
/// One class on the grid (e.g. GTP, GT3) with its ordered drivers and a class
/// color picked from the first driver. Replaces the legacy
/// <c>benofficial2.Standings.ClassNN</c> per-class buckets.
/// </summary>
public sealed record DriverClass(
    string Name,
    string Color,
    IReadOnlyList<Driver> Drivers);
