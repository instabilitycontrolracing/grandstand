using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Port 04 — the Timing Tower overlay window. A transparent, click-through,
/// topmost, no-taskbar WPF window pinned to the left edge of the primary
/// monitor, hosting the <see cref="TimingTowerRenderer"/>. Reuses the
/// click-through window mechanics established in Phase 2 / Port 01 / Port 03.
/// </summary>
public partial class TimingTowerWindow : Window
{
    const int GwlExStyle = -20;
    const int WsExTransparent = 0x00000020;
    const int WsExLayered = 0x00080000;

    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    // Tower window geometry: 432 px wide × full primary-monitor height, pinned
    // at (0,0). The card itself is 400 px wide with a 16 px breathing margin
    // on the left + right inside the window, and a 16 px top margin so the
    // gold accent hairline doesn't kiss y=0.
    const double WindowWidth = 432;
    const double CardOffsetX = 16;
    const double CardOffsetY = 18;
    const double CardBottomPad = 16;

    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly TimingTowerRenderer tower = new();
    // 220 ms matches TimingTowerRenderer.GateFadeAnimMs; the gate lets the
    // fade-out finish on screen before the window goes quiet.
    RepaintGate gate = new(220);

    public TimingTowerWindow()
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
        if (gate.ShouldPaint(PresentationState.TowerVisible, clock.Elapsed.TotalMilliseconds))
            Canvas.InvalidateVisual();
    }

    void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var info = e.Info;
        canvas.Clear(SKColors.Transparent);

        var snapshot = App.LatestSnapshot;
        double elapsedMs = clock.Elapsed.TotalMilliseconds;

        float originX = (float)CardOffsetX;
        float originY = (float)CardOffsetY;
        float availableHeight = info.Height - (float)(CardOffsetY + CardBottomPad);

        tower.Draw(canvas, originX, originY, availableHeight, snapshot, elapsedMs);
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        tower.Dispose();
        base.OnClosed(e);
    }
}
