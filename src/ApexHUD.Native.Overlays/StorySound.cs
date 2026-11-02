using System.Runtime.InteropServices;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Story-card sound cues (2026-10-20, Angelo): each card has its own short sound
/// (assets\audio\story_*.wav, synthesised for ApexHUD) plus a soft whoosh as it leaves.
/// Played asynchronously through Windows' own sound API on the default device - the same
/// path as the grid opener - so a sound can never stall a frame.
/// </summary>
public static class StorySound
{
    /// <summary>Master switch (preview tools turn it off and collect <see cref="OnPlay"/> instead).</summary>
    public static bool Enabled = true;
    /// <summary>Raised for every cue, even when <see cref="Enabled"/> is false (previews mix these into their video).</summary>
    public static Action<string>? OnPlay;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = false, EntryPoint = "PlaySound")]
    static extern bool PlaySoundNative(string? pszSound, IntPtr hmod, uint fdwSound);

    // 2026-10-30 (#604): PlaySound is "asynchronous" only for the playing - the call itself opens and reads the file,
    // and first stops whatever is still playing (it waits for the sound driver to let go). That ran on the render
    // thread, inside a frame. Now one small background thread makes every PlaySound call, in the order they were
    // asked for, so a stinger can never hold up a frame. Same sounds, same order, same timing to within a moment.
    static readonly System.Collections.Concurrent.BlockingCollection<(string? path, string? cue, uint flags)> queue = new(64);
    static Thread? worker;
    static readonly object workerGate = new();

    static bool PlaySound(string? path, IntPtr hmod, uint flags)
    {
        EnsureWorker();
        return queue.TryAdd((path, null, flags));       // full (64 waiting: never in practice) = that cue is skipped
    }

    static void EnsureWorker()
    {
        if (worker != null) return;
        lock (workerGate)
        {
            if (worker != null) return;
            var t = new Thread(() =>
            {
                foreach (var (path, cue, flags) in queue.GetConsumingEnumerable())
                {
                    try
                    {
                        // a cue's file is looked up here too (the first lookup of each checks the disk)
                        var p = cue != null ? SoundFiles.Resolve(cue + ".wav") : path;
                        if (cue == null || p != null) PlaySoundNative(p, IntPtr.Zero, flags);
                    }
                    catch { }
                }
            }) { IsBackground = true, Name = "Grandstand-Sound" };
            t.Start();
            worker = t;
        }
    }
    const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_FILENAME = 0x00020000;

    public static string? CueFor(StoryKind k) => k switch
    {
        StoryKind.RaceSoFar => "story_racesofar",
        StoryKind.YourRace => "story_yourrace",
        StoryKind.MoveOfRace => "story_moveofrace",
        StoryKind.FastestLaps => "story_fastestlaps",
        StoryKind.PitStops => "story_pitstops",
        StoryKind.Halfway => "story_halfway",
        StoryKind.WeatherWatch => "story_weather",
        StoryKind.DriverOfDay => "story_driverofday",
        StoryKind.CarContact => "story_contact",
        StoryKind.Limits => "story_limits",
        StoryKind.Cleanest => "story_clean",
        _ => null,
    };

    /// <summary>2026-10-26 (#207): the other graphics' stingers (battle card, fastest lap, yellow flag, pit callout,
    /// featured / custom cards, the tower's class change). Only while a replay plays - never in the driver's ears
    /// mid-race. Same sound path and fallbacks as <see cref="Play"/>.</summary>
    public static void PlayBroadcast(string cue)
    {
        // 2026-10-27 (Angelo: "stingers can and should play live also, just through the TV"): no longer replay-only.
        // Grandstand's sound is routed per-app to the TV cable (never the headset), so live is safe.
        Play(cue);
    }

    /// <summary>2026-10-27 (W8): a looping bed under the "going to break" card - only while a replay plays (never in the
    /// driver's ears), only when the file exists (break_bed.wav: the owner's own music). Any other cue stops it
    /// (Windows plays one PlaySound at a time).</summary>
    public static bool PlayBedLoop(string cue)
    {
        try { OnPlay?.Invoke(cue + " (loop)"); } catch { }
        if (!Enabled || !ApexHUD.Native.Common.ReplayState.IsReplay) return false;
        try
        {
            var path = SoundFiles.Resolve(cue + ".wav");
            if (path == null) return false;
            // (queued: "true" now means "asked to play" - the file exists; PlaySound itself only failed on a broken file)
            return PlaySound(path, IntPtr.Zero, SND_ASYNC | SND_NODEFAULT | SND_FILENAME | 0x0008 /* SND_LOOP */);
        }
        catch { return false; }
    }

    /// <summary>Stops whatever PlaySound is playing (the break bed).</summary>
    public static void StopBed()
    {
        if (!Enabled) return;
        try { PlaySound(null, IntPtr.Zero, 0); } catch { }
    }

    /// <summary>#603 (2026-10-30): when the last cue started by <see cref="Play"/> ends (Environment.TickCount64, ms). Windows plays
    /// one PlaySound at a time - a new cue cuts the one playing - so cues that are a nicety (the viewer tier stingers) wait
    /// for quiet with <see cref="PlayIfQuiet"/> instead of chopping a story card's or a battle card's sound.</summary>
    public static long BusyUntilMs { get; private set; }

    /// <summary>Forgets the cue that is sounding (preview tools, between runs).</summary>
    public static void ResetBusy() => BusyUntilMs = 0;

    /// <summary>Test clock for the preview tools (null = Environment.TickCount64).</summary>
    public static Func<long>? Clock;
    static long Now => Clock?.Invoke() ?? Environment.TickCount64;

    static readonly Dictionary<string, long> lengths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The cue's length in ms from its WAV header (cached; 4.3 s when it can't be read).</summary>
    public static long LengthMs(string cue)
    {
        lock (lengths)
        {
            if (lengths.TryGetValue(cue, out long ms)) return ms;
            ms = 4300;
            try
            {
                var path = SoundFiles.Resolve(cue + ".wav");
                if (path != null)
                {
                    using var f = System.IO.File.OpenRead(path);
                    using var r = new System.IO.BinaryReader(f);
                    // RIFF / WAVE: walk the chunks for 'fmt ' (byte rate) and 'data' (size)
                    f.Position = 12;
                    int byteRate = 0;
                    while (f.Position + 8 <= f.Length)
                    {
                        string id = new string(r.ReadChars(4));
                        int size = r.ReadInt32();
                        long next = f.Position + size + (size & 1);
                        if (id == "fmt ") { f.Position += 8; byteRate = r.ReadInt32(); }
                        else if (id == "data" && byteRate > 0) { ms = (long)(size * 1000.0 / byteRate); break; }
                        f.Position = next;
                    }
                }
            }
            catch { }
            lengths[cue] = ms;
            return ms;
        }
    }

    /// <summary>#603: plays the cue only if no other cue is still sounding; false when it was skipped (never chops a sound).</summary>
    public static bool PlayIfQuiet(string cue)
    {
        if (Now < BusyUntilMs) { try { OnPlay?.Invoke(cue + " (skipped: another cue playing)"); } catch { } return false; }
        Play(cue);
        return true;
    }

    public static void Play(string? cue)
    {
        if (cue == null) return;
        BusyUntilMs = Now + LengthMs(cue);
        try { OnPlay?.Invoke(cue); } catch { }
        if (!Enabled) return;
        try
        {
            // personal assets\audio -> Documents\iRacing\Grandstand\audio -> assets\audio_public (2026-10-24);
            // the file is found and played on the sound thread (#604)
            EnsureWorker();
            queue.TryAdd((null, cue, SND_ASYNC | SND_NODEFAULT | SND_FILENAME));
        }
        catch { /* sound is a nicety - never let it affect the overlay */ }
    }
}
