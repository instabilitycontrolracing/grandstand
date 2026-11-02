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
/// Port 06 — the Battle Card overlay window. Transparent, click-through,
/// topmost, no-taskbar, centred on the primary monitor, sized to comfortably
/// hold the 1000×280 card with breathing room for the entrance translate.
/// Reuses the click-through window mechanics established Phase 2 / Port 01 / 03 / 04 / 05.
/// </summary>
public partial class BattleCardWindow : Window
{
    const int GwlExStyle = -20;
    const int WsExTransparent = 0x00000020;
    const int WsExLayered = 0x00080000;

    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    // 1280×340 to match the legacy window slot, with 140 px of horizontal pad
    // and 30 px of vertical pad inside it so the entrance translate (14 px) and
    // top-bar glow have headroom and the card sits dead-centre.
    const double WindowWidth = 1280;
    const double WindowHeight = 340;
    const double CardOffsetX = (WindowWidth - BattleCardRenderer.CardWidth) / 2.0;
    const double CardOffsetY = (WindowHeight - BattleCardRenderer.CardHeight) / 2.0;

    // Pin the window's bottom edge 8 px above the screen bottom — broadcast
    // lower-third feel. The card itself sits CardOffsetY (≈30 px) above the
    // window bottom, so the card's bottom edge floats ~38 px from the screen.
    const double BottomMargin = 8;

    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly BattleCardRenderer renderer = new();

    public BattleCardWindow()
    {
        InitializeComponent();

        Width = WindowWidth;
        Height = WindowHeight;
        // True TV lower-third: anchor the window's bottom edge to within a few
        // px of the screen bottom so the card reads as broadcast lower-third
        // chrome. With the card centred inside the window (CardOffsetY ≈ 30 px
        // of bottom pad), the card's bottom edge ends up ~38 px above the
        // screen edge — close enough for a clean lower-third.
        Left = (SystemParameters.PrimaryScreenWidth - WindowWidth) / 2.0;
        Top = SystemParameters.PrimaryScreenHeight - WindowHeight - BottomMargin;
        if (Top < 0) Top = 0;

        // Honour the launcher --battle-testloop flag (a Port 06 affordance for
        // verifying visuals without staging a live race). The renderer's
        // TestLoop is null otherwise → live evaluation runs.
        if (App.BattleTestLoopRequested)
            renderer.TestLoop = new BattleTestLoop();

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

    void OnRendering(object? sender, EventArgs e) => Canvas.InvalidateVisual();

    void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var snapshot = App.LatestSnapshot;
        double elapsedMs = clock.Elapsed.TotalMilliseconds;
        renderer.Draw(canvas, (float)CardOffsetX, (float)CardOffsetY, snapshot, elapsedMs);

        // Publish the cross-window battle-active signal that drives the whole
        // visibility state machine (PresentationState reads this every frame).
        PresentationState.IsBattleActive = renderer.IsActive;
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        renderer.Dispose();
        base.OnClosed(e);
    }
}
