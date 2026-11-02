using System.Windows;
using System.Windows.Media;
using ApexHUD.Native.Common;
using ApexHUD.Native.Data;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Application entry. Owns the single <see cref="RaceStateService"/> the
/// overlay windows bind to; lives for the process lifetime. Windows pull the
/// latest <see cref="RaceSnapshot"/> via <see cref="LatestSnapshot"/> each
/// frame rather than subscribing to the per-tick event, so the WPF render
/// thread reads lock-free and the IRSDKSharper background thread never
/// crosses dispatcher boundaries.
///
/// <para>Also hooks <c>CompositionTarget.Rendering</c> once on startup so
/// <see cref="PresentationState.Tick"/> runs every frame on the render thread,
/// keeping the <c>lap1Complete</c> latch in lock-step with whatever the windows
/// paint.</para>
/// </summary>
public partial class App : Application
{
    /// <summary>Created lazily on startup; null only between construction and <see cref="OnStartup"/>.</summary>
    public static RaceStateService? RaceState { get; private set; }

    /// <summary>
    /// Optional external snapshot source. Default null — the WPF overlay never
    /// sets it, so its behaviour is unchanged. The Phase-B native GPU host
    /// (<c>ApexHUD.Native.GpuHost</c>) runs in a process where the WPF
    /// <see cref="RaceState"/> singleton is never started; it sets this each
    /// frame so the shared visibility logic (<see cref="PresentationState"/>) and
    /// scene rotation (<see cref="SceneDirector"/>) — both of which read
    /// <see cref="LatestSnapshot"/> — work against the host's own data feed. This
    /// is a tiny, inert seam: when null, the original resolution path is used.
    /// </summary>
    public static RaceSnapshot? SnapshotOverride { get; set; }

    /// <summary>The most recent snapshot. Always non-null after <see cref="OnStartup"/>.</summary>
    public static RaceSnapshot LatestSnapshot =>
        SnapshotOverride ?? RaceState?.Current ?? RaceSnapshot.Disconnected;

    /// <summary>
    /// True when the process was started with <c>--battle-testloop</c>. The
    /// battle window attaches a scripted <see cref="BattleTestLoop"/> instead
    /// of live detection — used for verifying the visuals when iRacing isn't
    /// running or a close same-class duel is hard to stage.
    /// </summary>
    public static bool BattleTestLoopRequested { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Parse our small launcher-flag surface before any window construction.
        foreach (var arg in e.Args)
        {
            if (string.Equals(arg, "--battle-testloop", StringComparison.OrdinalIgnoreCase))
                BattleTestLoopRequested = true;
        }

        RaceState = new RaceStateService();
        RaceState.Start();
        CompositionTarget.Rendering += OnRenderingFrame;
        base.OnStartup(e);

        // Spawn every overlay window. Each one owns its own SkiaSharp render
        // loop and reads RaceState.Current per frame (Port 03 decision: pull
        // per frame, no dispatcher marshalling). Visibility coordination flows
        // through PresentationState — every window reads the same derived
        // gates so the suite switches together. MainWindow is shown last so it
        // sits above StandingsTickerWindow in z-order — the bar's watch
        // overhangs onto the ticker's top edge and must paint on top.
        // Tower lane: the tower window paints the standings; the splash
        // window paints over the same lane during scene transitions. The
        // splash is created RIGHT AFTER the tower so it sits above the tower
        // in z-order — covering the rebuild while the next scene is being
        // selected, then fading out to reveal it.
        new TimingTowerWindow().Show();
        new TimingTowerSplashWindow().Show();
        new FeaturedCardWindow().Show();
        new BattleCardWindow().Show();
        new StandingsTickerWindow().Show();
        new StartingGridWindow().Show();
        new FastestLapWindow().Show();
        new PitCalloutWindow().Show();
        new MainWindow().Show();

        // Background pre-warm so the first tower transition is not fighting cold
        // init (cold disk reads, JPEG codec, fonts). Finishes long before the first
        // transition (~20 s in) and never touches the render thread.
        System.Threading.Tasks.Task.Run(() =>
        {
            try { Fonts.WarmUp(); SceneTransitions.WarmUp(); }
            catch { }
        });
    }

    static void OnRenderingFrame(object? sender, EventArgs e)
    {
        var snap = LatestSnapshot;
        PresentationState.Tick(snap);
        SceneDirector.Tick(snap);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CompositionTarget.Rendering -= OnRenderingFrame;
        RaceState?.Dispose();
        RaceState = null;
        base.OnExit(e);
    }
}
