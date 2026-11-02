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
/// Port 05 — the Featured Driver Card overlay window. A transparent,
/// click-through, topmost, no-taskbar WPF window pinned to the bottom-centre of
/// the primary monitor, hosting the <see cref="FeaturedCardRenderer"/>. Reuses
/// the click-through window mechanics established in Phase 2 / Port 01 / 03 / 04.
/// </summary>
public partial class FeaturedCardWindow : Window
{
    const int GwlExStyle = -20;
    const int WsExTransparent = 0x00000020;
    const int WsExLayered = 0x00080000;

    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    // Window geometry: 768×254 — the legacy window size, preserving every CSS
    // pixel measurement inside it. The card itself is 720×210 sitting with 24 px
    // horizontal pad and 22 px top / 22 px bottom inside the window (the legacy
    // body padding was 16 px; we go 24 to leave a touch more glow headroom).
    const double WindowWidth = 768;
    const double WindowHeight = 254;
    const double CardOffsetX = 24;
    const double CardOffsetY = 22;

    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly FeaturedCardRenderer renderer = new();
    // 280 ms matches FeaturedCardRenderer.GateFadeAnimMs; gate keeps the
    // window painting through the outer cross-fade, then goes quiet.
    RepaintGate gate = new(280);

    public FeaturedCardWindow()
    {
        InitializeComponent();

        Width = WindowWidth;
        Height = WindowHeight;
        // Centre horizontally; pin near the bottom of the primary monitor with
        // a small margin so the card hovers like a broadcast lower-third.
        Left = (SystemParameters.PrimaryScreenWidth - WindowWidth) / 2.0;
        // Bottom-align the card glass with the timing tower bottom edge (screenH - 16).
        // Glass bottom inside window = CardOffsetY(22) + CardHeight(189) - 10.5 trim = 200.5 px,
        // so Top = screenH - 16 - 200.5 = screenH - WindowHeight + 37.5.
        Top = SystemParameters.PrimaryScreenHeight - WindowHeight + 37.5;

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
        if (gate.ShouldPaint(PresentationState.FeaturedVisible, clock.Elapsed.TotalMilliseconds))
            Canvas.InvalidateVisual();
    }

    void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var snapshot = App.LatestSnapshot;
        double elapsedMs = clock.Elapsed.TotalMilliseconds;
        renderer.Draw(canvas, (float)CardOffsetX, (float)CardOffsetY, snapshot, elapsedMs);
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        renderer.Dispose();
        base.OnClosed(e);
    }
}
