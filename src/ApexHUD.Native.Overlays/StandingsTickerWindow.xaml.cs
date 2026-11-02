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
/// Standings Ticker overlay window. Pinned flush directly under the session
/// bar plate (the plate ends at primary-monitor y=110; the ticker sits with
/// its top edge at y=110 with zero gap). The watch hangs ~18 px below the
/// plate, so its bottom half overlaps the top ~18 px of the ticker — that
/// overlap is intentional (Session 16 brief: "the watch face should overlap
/// the top of the ticker slightly"), and MainWindow is shown LAST in
/// <see cref="App.OnStartup"/> so the bar window sits above the ticker in
/// z-order and the watch paints on top. Transparent, click-through, topmost,
/// no-taskbar — same mechanics as every other overlay in the suite. Visible
/// only when <see cref="PresentationState.TickerVisible"/>; the renderer
/// owns the fade.
/// </summary>
public partial class StandingsTickerWindow : Window
{
    const int GwlExStyle = -20;
    const int WsExTransparent = 0x00000020;
    const int WsExLayered = 0x00080000;

    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    // Ticker is rendered full-screen-wide so it can flush-attach under the
    // session bar in both default-width (centred 976) and battle full-width
    // states. The renderer paints its 44 px tall strip at the top of the
    // window; the bottom of the window is empty transparent space.
    const double WindowHeight = 60;
    // Flush under the session bar plate. MainWindow places the plate at
    // window-y BarPlateTop=18 + plate height 92 = 110 ⇒ plate bottom is at
    // primary-monitor y=110. The ticker's top edge sits at that same y, so
    // the watch's bottom half (which reaches y≈128) overlaps the top ~18 px
    // of the ticker — the desired broadcast layering.
    const double TopOnPrimary = 110;
    const double CardOffsetX = 0;
    const double CardOffsetY = 0;

    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly StandingsTickerRenderer ticker = new();
    // 320 ms matches StandingsTickerRenderer.FadeAnimMs.
    RepaintGate gate = new(320);

    public StandingsTickerWindow()
    {
        InitializeComponent();

        Width = SystemParameters.PrimaryScreenWidth;
        Height = WindowHeight;
        Left = 0;
        Top = TopOnPrimary;

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
        if (gate.ShouldPaint(PresentationState.TickerVisible, clock.Elapsed.TotalMilliseconds))
            Canvas.InvalidateVisual();
    }

    void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var info = e.Info;
        canvas.Clear(SKColors.Transparent);

        var snapshot = App.LatestSnapshot;
        double elapsedMs = clock.Elapsed.TotalMilliseconds;
        // Render at full primary-monitor width so the ticker spans the screen
        // flush-edge to flush-edge when battle is active (Session 16 brief).
        ticker.Draw(canvas, (float)CardOffsetX, (float)CardOffsetY, info.Width, snapshot, elapsedMs);
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        ticker.Dispose();
        base.OnClosed(e);
    }
}
