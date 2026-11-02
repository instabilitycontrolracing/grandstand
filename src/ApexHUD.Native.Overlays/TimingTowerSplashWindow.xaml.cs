using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Transparent, click-through, topmost splash window pinned over the timing
/// tower lane. Hosts <see cref="TimingTowerSplashRenderer"/>; visibility is
/// driven entirely by <see cref="SceneDirector.SplashActive"/> via
/// <see cref="RepaintGate"/>. Shown immediately after the tower window in
/// <see cref="App.OnStartup"/> so it sits above the tower in z-order — the
/// splash covers the rebuild, then fades to reveal the new board.
///
/// <para>Window geometry matches the tower lane exactly (432 × primary-monitor
/// height @ (0,0)) so the splash composite stays inside the same visual lane
/// the tower occupies. The session bar window is shown later in startup, so it
/// continues to sit above the splash where they overlap — the watch + clock
/// stay visible during transitions, matching broadcast intent.</para>
/// </summary>
public partial class TimingTowerSplashWindow : Window
{
    const int GwlExStyle = -20;
    const int WsExTransparent = 0x00000020;
    const int WsExLayered = 0x00080000;

    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    // Match the tower window dimensions (TimingTowerWindow.WindowWidth = 432,
    // primary-monitor-height, pinned at (0,0)). Kept as local constants because
    // the tower's constants are private; if a future polish pass widens the
    // tower lane, these two values move together.
    const double WindowWidth = 432;
    const double CardOffsetX = 16;
    const double CardOffsetY = 16;
    const double CardBottomPad = 16;

    readonly TimingTowerSplashRenderer splash = new();
    // Fade buffer just past SceneDirector.SplashFadeMs so the renderer settles
    // past floating-point epsilon before the window goes quiet.
    RepaintGate gate = new(SceneDirector.SplashFadeMs);

    public TimingTowerSplashWindow()
    {
        InitializeComponent();

        Width = WindowWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Left = 0;
        Top = 0;

        SourceInitialized += OnSourceInitialized;
        KeyDown += OnKeyDown;
        CompositionTarget.Rendering += OnRendering;
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, exStyle | WsExTransparent | WsExLayered);
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Application.Current.Shutdown();
    }

    void OnRendering(object? sender, EventArgs e)
    {
        if (gate.ShouldPaint(SceneDirector.SplashActive, SceneDirector.NowMs))
            Canvas.InvalidateVisual();
    }

    void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var info = e.Info;
        canvas.Clear(SKColors.Transparent);

        if (!SceneDirector.SplashActive) return;

        float originX = (float)CardOffsetX;
        float originY = (float)CardOffsetY;
        float width = (float)(WindowWidth - 2 * CardOffsetX);
        float height = info.Height - (float)(CardOffsetY + CardBottomPad);

        splash.Draw(canvas, originX, originY, width, height, App.LatestSnapshot, SceneDirector.NowMs);
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        splash.Dispose();
        base.OnClosed(e);
    }
}
