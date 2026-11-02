namespace ApexHUD.Native.Common;

/// <summary>
/// 2026-10-27: one place that knows whether the owner is driving live right now (#338: story cards are held during a
/// battle while he drives). Fed by the data service every tick. (It was first written to mute sounds while driving, #309;
/// Angelo reversed that - Grandstand's sounds go to the TV cable only, so they may play live. No sound uses it.)
/// <para><b>Driving live</b> = the player's car is on track (iRacing <c>IsOnTrack</c> or <c>IsOnTrackCar</c>), no replay is
/// playing (<c>IsReplayPlaying</c> false), the replay is at the live end (<c>ReplayFrameNumEnd</c> &lt; 30 frames, so a
/// paused rewound replay with the car on track does not count as live, the same test the replay director uses) and no
/// saved replay file is loaded (a file can't be driven). It latches: after the last driving tick it stays true for
/// <see cref="ReleaseMs"/> so a tow / reset flicker does not count as leaving the car.</para>
/// </summary>
public static class DrivingState
{
    /// <summary>How long the gate stays shut after the last tick that looked like driving.</summary>
    public const long ReleaseMs = 3000;

    static long lastDrivingMs = long.MinValue / 2;
    static volatile bool connected;

    /// <summary>The test itself (pure, for the trigger checks). <paramref name="framesBehindLive"/> -1 = unknown, which
    /// counts as live (older builds without ReplayFrameNumEnd: then IsReplayPlaying alone decides).</summary>
    public static bool LooksLikeDriving(bool isOnTrack, bool isOnTrackCar, bool replayPlaying, int framesBehindLive, bool replayFile)
        => (isOnTrack || isOnTrackCar) && !replayPlaying && !replayFile && framesBehindLive < 30;

    /// <summary>Called by the data service once per telemetry tick.</summary>
    public static void Update(bool isOnTrack, bool isOnTrackCar, bool replayPlaying, int framesBehindLive, bool replayFile, long nowMs)
    {
        connected = true;
        if (LooksLikeDriving(isOnTrack, isOnTrackCar, replayPlaying, framesBehindLive, replayFile))
            Interlocked.Exchange(ref lastDrivingMs, nowMs);
    }

    /// <summary>iRacing went away: nobody is driving.</summary>
    public static void Disconnected() { connected = false; Interlocked.Exchange(ref lastDrivingMs, long.MinValue / 2); }

    /// <summary>True while the owner is (or within <see cref="ReleaseMs"/> was) driving live: no sound except the pre-race opener.</summary>
    public static bool DrivingLive => IsDrivingAt(Environment.TickCount64);

    public static bool IsDrivingAt(long nowMs) => connected && nowMs - Interlocked.Read(ref lastDrivingMs) <= ReleaseMs;

    /// <summary>Test hook: forget everything.</summary>
    public static void Reset() { connected = false; Interlocked.Exchange(ref lastDrivingMs, long.MinValue / 2); }
}
