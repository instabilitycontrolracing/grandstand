using System.Windows.Media;
using System.Windows.Threading;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Plays the starting-grid opener theme inside Grandstand (2026-10-24). It used to start a hidden
/// PowerShell script from C:\SimRacing\Tools, which only exists on Angelo's PC and looks suspicious to
/// antivirus. Same behaviour: default sound device, volume 0.85, at most <c>maxSeconds</c>, fading out
/// over the last <c>fadeSeconds</c>, on its own background thread so it can never stall a frame.
/// <see cref="StartedUtcTicks"/> is the exact instant the music started (the grid intro syncs to it).
/// </summary>
public static class OpenerAudio
{
    static long startedTicks;
    public static long StartedUtcTicks => Interlocked.Read(ref startedTicks);
    static int playing;
    static volatile bool stopRequested;
    /// <summary>The opener (the full-screen grid intro's music) is playing - the race engineer holds race control until it ends.</summary>
    public static bool IsPlaying => Volatile.Read(ref playing) == 1;

    /// <summary>Quick fade-out and stop (2026-10-25: the replay was rewound / skipped through the opener).</summary>
    public static void Stop() { if (Volatile.Read(ref playing) == 1) stopRequested = true; }

    public static bool Play(string file, double maxSeconds = 60, double fadeSeconds = 1.0, double volume = 0.85)
    {
        if (!System.IO.File.Exists(file)) return false;
        if (Interlocked.Exchange(ref playing, 1) == 1) return false;     // one opener at a time
        Interlocked.Exchange(ref startedTicks, 0);
        stopRequested = false;
        var t = new Thread(() =>
        {
            MediaPlayer? mp = null;
            try
            {
                mp = new MediaPlayer();
                mp.Open(new Uri(file));
                mp.Volume = volume;
                var t0 = DateTime.UtcNow;
                while (!mp.NaturalDuration.HasTimeSpan && (DateTime.UtcNow - t0).TotalSeconds < 3) Pump(30);
                double len = mp.NaturalDuration.HasTimeSpan ? Math.Min(mp.NaturalDuration.TimeSpan.TotalSeconds, maxSeconds) : maxSeconds;
                mp.Play();
                // TV stream: the game / radio sound on the stream fades out under the music (2026-10-23)
                Task.Run(() => GameDuck.Begin());
                bool restoring = false;
                var start = DateTime.UtcNow;
                Interlocked.Exchange(ref startedTicks, start.Ticks);
                double el;
                while ((el = (DateTime.UtcNow - start).TotalSeconds) < len)
                {
                    if (stopRequested)
                    {
                        for (int i = 10; i >= 0; i--) { mp.Volume = volume * i / 10.0; Pump(25); }
                        break;
                    }
                    if (len - el < fadeSeconds) mp.Volume = volume * Math.Max(0, (len - el) / fadeSeconds);
                    // 2026-10-27 (#394): the game stays silent for the whole song and comes straight back the moment it ends
                    if (!restoring && len - el < 0.15) { restoring = true; Task.Run(() => GameDuck.End()); }
                    Pump(30);
                }
            }
            catch { /* audio is a nicety - never let it affect the overlay */ }
            finally
            {
                try { mp?.Stop(); mp?.Close(); } catch { }
                Task.Run(() => GameDuck.End());
                Interlocked.Exchange(ref playing, 0);
            }
        }) { IsBackground = true, Name = "Grandstand-Opener" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return true;
    }

    static void Pump(int ms)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Thread.Sleep(ms);
    }
}
