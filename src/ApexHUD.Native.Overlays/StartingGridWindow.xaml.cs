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
/// Port 07 — the Starting Grid overlay window. Transparent, click-through,
/// topmost, no-taskbar, centred on the primary monitor at the legacy
/// <c>1100×830</c> slot. Reuses the click-through mechanics established Phase 2
/// / Port 01 / 03 / 04 / 05 / 06.
///
/// <para>The window is always alive; the renderer phase-gates visibility, so
/// the grid only paints when <see cref="ApexHUD.Native.Common.RacePhase"/> is
/// <c>PreRace</c> or <c>PostRace</c> (with a ~520 ms cross-fade on a phase
/// flip).</para>
/// </summary>
public partial class StartingGridWindow : Window
{
    const int GwlExStyle = -20;
    const int WsExTransparent = 0x00000020;
    const int WsExLayered = 0x00080000;

    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    const double WindowWidth = 1100;
    const double WindowHeight = 830;
    const double CardOffsetX = (WindowWidth - StartingGridRenderer.CardWidth) / 2.0;
    const double CardOffsetY = (WindowHeight - StartingGridRenderer.CardHeight) / 2.0;

    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly StartingGridRenderer grid = new();
    // 520 ms matches StartingGridRenderer.FadeAnimMs.
    RepaintGate gate = new(520);

    public StartingGridWindow()
    {
        InitializeComponent();

        Width = WindowWidth;
        Height = WindowHeight;
        // Centred on the primary monitor — the legacy window was at (410, 230)
        // on a 1920×1080 monitor, which is the visual centre. We replicate
        // "centred" without baking in 1920-wide assumptions.
        Left = (SystemParameters.PrimaryScreenWidth - WindowWidth) / 2.0;
        Top = Math.Max(0, (SystemParameters.PrimaryScreenHeight - WindowHeight) / 2.0);

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
        if (gate.ShouldPaint(PresentationState.GridVisible, clock.Elapsed.TotalMilliseconds))
            Canvas.InvalidateVisual();
    }

    void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var snapshot = App.LatestSnapshot;
        double elapsedMs = clock.Elapsed.TotalMilliseconds;
        grid.Draw(canvas, (float)CardOffsetX, (float)CardOffsetY, snapshot, elapsedMs);
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        grid.Dispose();
        base.OnClosed(e);
    }
}
