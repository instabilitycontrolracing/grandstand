namespace ApexHUD.Native.Common;

/// <summary>
/// One row of the leaderboard. Mirrors the shapes the legacy overlays consume
/// (the IREP-flavored <c>_leaderboard[]</c> + the benofficial2 standings rows)
/// flattened into a single immutable record. <see cref="RaceSnapshot"/> groups
/// these by class.
/// </summary>
/// <remarks>
/// All time fields are in seconds (broadcast formatting is in
/// <see cref="LapTimeFormat"/>). Gap fields are nullable so overlays can
/// distinguish "no data yet" from "0.0 s".
/// </remarks>
public sealed record Driver(
    int CarIdx,
    string Name,
    string AbbrevName,
    string Initials,
    string CarNumber,
    string CarName,
    string CarBrand,
    string ClassName,
    string ClassColor,
    string LicenceString,
    string LicenceColor,
    int Position,
    int PositionInClass,
    int CurrentLap,
    float LapDistPct,
    int LapsCompleted,
    int LapDifference,
    float BestLapTime,
    float LastLapTime,
    // 2026-10-24: both gaps are per CLASS and come from iRacing's timing (CarIdxF2Time, with
    // CarIdxEstTime / lap-distance fallbacks): GapToLeaderSeconds = behind the class leader,
    // IntervalSeconds = behind the car directly ahead in the class order. Practice/qualifying:
    // fastest-lap differences.
    double? GapToLeaderSeconds,
    double? IntervalSeconds,
    bool IsInPit,
    bool IsConnected,
    bool IsPaceCar,
    bool IsSpectator,
    int IRating,
    // 2026-10-18: qualifying fastest time (seconds; 0 = none) from the session's
    // QualifyResultsInfo - shown on the pre-race starting-grid hero board.
    float QualifyTime = 0f,
    // 2026-10-20: incident points so far this session (-1 = not known). The player's comes
    // live from telemetry, everyone else's from iRacing's session info (it updates every few seconds).
    int Incidents = -1,
    // 2026-10-20: all four wheels off the track right now (iRacing TrackSurface = OffTrack).
    bool OffTrack = false,
    // 2026-10-25: for the real liveries (Trading Paints files live in Documents\iRacing\paint\<CarPath>\car_<UserId>.tga,
    // or car_team_<TeamId>.tga for team cars)
    string CarPath = "",
    int UserId = 0,
    int TeamId = 0,
    // 2026-10-25: the driver's iRacing paint scheme, for iRacing's car renderer (session info CarDesignStr
    // "pattern,colour1,colour2,colour3", CarNumberDesignStr "pattern,slant,colour1,colour2,colour3", sponsors "s1,s2")
    string CarDesign = "",
    string NumberDesign = "",
    string Sponsors = "",
    // 2026-10-25: disqualified this session (iRacing's results: reason out "Disqualified", e.g. too many incident points)
    bool Disqualified = false,
    // 2026-10-29 (car parity): iRacing's CarID - with CarPath, the key into CarCatalog (0 = not known)
    int CarId = 0);
