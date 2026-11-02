namespace ApexHUD.Native.Common;

/// <summary>
/// 2026-10-25 (Angelo: "when I rewind through to the beginning the intro plays as I rewind - I only want it to trigger
/// when it's playing, not rewinding"). How the replay is moving right now, written by the data service every tick.
/// </summary>
public static class ReplayState
{
    static volatile bool replay, slow;
    static volatile int speed = 1;
    static long steadySinceMs = -1_000_000;   // when it last started playing forward at normal speed (or last jumped)
    static double sessionTime;

    public static bool IsReplay => replay;
    public static int Speed => speed;
    /// <summary>iRacing's SessionTime (seconds into the session) at the last tick.</summary>
    public static double SessionTime => Volatile.Read(ref sessionTime);

    /// <summary>Rewinding, fast-forwarding or slow motion right now.</summary>
    public static bool Scrubbing => replay && (speed != 1 || slow);

    /// <summary>Playing forward at normal speed for 2 s+ (not just after a skip or a jump to another session).</summary>
    public static bool PlayingNormally => !replay || (!Scrubbing && Environment.TickCount64 - Interlocked.Read(ref steadySinceMs) > 2000);

    public static void Set(bool isReplay, int playSpeed, bool slowMotion)
    {
        bool wasScrub = Scrubbing;
        replay = isReplay; speed = playSpeed; slow = slowMotion;
        if (wasScrub && !Scrubbing) Interlocked.Exchange(ref steadySinceMs, Environment.TickCount64);
    }
    public static void SetTime(double st) => Volatile.Write(ref sessionTime, st);

    // 2026-10-26 (#235): where the replay is against live, for the replay wipe and the REPLAY tag
    static volatile int framesBehind = -1;
    static volatile bool replayFile;
    static int jumps;

    /// <summary>Frames between the replay and the live end (iRacing ReplayFrameNumEnd, 60 a second); -1 unknown.
    /// In a saved replay file this is the distance to the end of the file.</summary>
    public static int FramesBehindLive => framesBehind;
    /// <summary>A saved replay file is loaded (WeekendInfo.SimMode "replay"), not a live session.</summary>
    public static bool IsReplayFile => replayFile;
    /// <summary>Counts <see cref="Jumped"/> calls (a skip or a rewind moved the session clock at once).</summary>
    public static int JumpCount => Volatile.Read(ref jumps);

    public static void SetFramesBehind(int frames) => framesBehind = frames;
    public static void SetReplayFile(bool file) => replayFile = file;
    public static void Jumped()
    {
        Interlocked.Exchange(ref steadySinceMs, Environment.TickCount64);
        Interlocked.Increment(ref jumps);
    }
}
