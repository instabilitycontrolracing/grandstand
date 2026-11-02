namespace ApexHUD.Native.Common;

/// <summary>
/// 2026-10-24 (Angelo: "anytime the car is in an onboard I want to see a very refined throttle, brake, steering,
/// speed and gear"): what the camera is looking at and the inputs we can show for that car.
/// iRacing sends the full set (pedals, wheel angle, speed) only for the player's own car; for any other car it
/// sends steering, gear and RPM, and speed is worked out from how fast the car moves round the lap.
/// </summary>
public sealed record OnboardData(
    int CamCarIdx,
    int CamGroup,
    string CamGroupName,
    /// <summary>The camera is inside / on the car (cockpit, roll bar, nose, gearbox...).</summary>
    bool IsOnboard,
    bool IsPlayerCar,
    /// <summary>True when throttle / brake are real for this car (the player's own, and moving in replays).</summary>
    bool HasPedals,
    float Throttle,
    float Brake,
    float Clutch,
    /// <summary>Wheel angle in radians, positive = left (iRacing's sign).</summary>
    float SteerRad,
    /// <summary>Lock-to-lock half range in radians (0 = unknown).</summary>
    float SteerMaxRad,
    float SpeedMs,
    int Gear,
    float Rpm,
    float ShiftRpm,
    float RedlineRpm,
    bool Metric,
    /// <summary>Where the camera car is round the lap, 0..1 (-1 unknown).</summary>
    float LapPct = -1f,
    /// <summary>Player only: live time versus the best lap (s, + = slower), iRacing's LapDeltaToBestLap.</summary>
    float DeltaBest = 0f,
    bool DeltaOk = false,
    /// <summary>Player only: sideways and forward acceleration (m/s^2; iRacing LatAccel + left, LongAccel + forward).</summary>
    float LatAccel = 0f,
    float LongAccel = 0f,
    bool HasG = false,
    /// <summary>Player only: ABS letting the brake off / traction control cutting the throttle right now (2026-10-25).</summary>
    bool AbsActive = false,
    bool TcActive = false,
    bool HasAbsTc = false,
    /// <summary>The car has ABS (then AbsActive is ABS working); without it AbsActive is a lockup (2026-10-25).</summary>
    bool CarHasAbs = false,
    bool CarHasTc = true)
{
    /// <summary>2026-10-26 (#234): the camera GROUP's own name ("TV1", "Blimp"...), even when <see cref="CamGroupName"/>
    /// holds the live camera's name inside it.</summary>
    public string GroupOnlyName { get; init; } = "";

    public static readonly OnboardData None = new(-1, -1, "", false, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, true);
}
