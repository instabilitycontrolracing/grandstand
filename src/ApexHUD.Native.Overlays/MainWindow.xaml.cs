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
/// Port 03 — the Session Bar overlay. A transparent, click-through, topmost,
/// no-taskbar window spanning the primary monitor's top edge. Hosts the
/// deep-glass session bar (via <see cref="SessionBarRenderer"/>) and the
/// Rolex-style watch centrepiece (via <see cref="WatchRenderer"/>) sitting in
/// the bar's alcove. The legacy bar window was 1936×230 @ (0,−8); we adapt to
/// the primary monitor's actual width (multi-monitor placement remains a
/// later, deliberate decision — see DECISIONS.md).
/// </summary>
public partial class MainWindow : Window
{
    const int GwlExStyle = -20;
    const int WsExTransparent = 0x00000020;
    const int WsExLayered = 0x00080000;

    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    // Window geometry: primary-monitor-wide × 230 tall (legacy window), pinned
    // to the very top of the screen.
    const double WindowHeight = 230;

    // Bar plate dimensions inside the window — fixed to the legacy 976×92
    // dimensions and horizontally centred. The watch overhangs above and
    // below the plate, which is why the window is taller than the plate.
    //
    // The plate sits 34 px below the window's top so the watch (centred on
    // the plate, radius 64) has ~16 px breathing room above its bezel — see
    // POLISH_BACKLOG.md item #1: at the original 14 px the watch extended to
    // y=-4 and clipped against the monitor's top edge.
    const double BarPlateTop = 18;

    // Battle-mode expansion: the bar grows from its default 976 px centred
    // width to the full primary-monitor width when a battle is active, then
    // shrinks back when the battle ends. Smooth 400 ms cross-fade so the
    // swap reads as a deliberate broadcast cut.
    const double BarExpandAnimMs = 400;

    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly WatchRenderer watch = new();
    readonly SessionBarRenderer bar = new();

    bool wasFullWidth;
    double widthFlipAtMs = -1;

    public MainWindow()
    {
        InitializeComponent();

        Width = SystemParameters.PrimaryScreenWidth;
        Height = WindowHeight;
        Left = 0;
        Top = 0;

        SourceInitialized += OnSourceInitialized;
        KeyDown += OnKeyDown;
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>
    /// Make the window ignore mouse input so clicks fall through to iRacing
    /// underneath. Must run after the HWND exists.
    /// </summary>
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

    // Drive a continuous repaint: smooth second-hand sweep, weather animations,
    // flag pulses, progress bar advancing every tick.
    void OnRendering(object? sender, EventArgs e) => Canvas.InvalidateVisual();

    void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var info = e.Info;
        canvas.Clear(SKColors.Transparent);

        var snapshot = App.LatestSnapshot;
        double elapsedMs = clock.Elapsed.TotalMilliseconds;

        // Bar plate width animates between the default 976 px and the full
        // primary-monitor width on a battle-mode flip. Always centred
        // horizontally, so the watch (drawn on top) stays on the screen's
        // vertical midline regardless of width.
        float plateWidth = ComputeBarWidth(info.Width, elapsedMs);
        float plateHeight = SessionBarRenderer.BarHeight;
        float plateX = (info.Width - plateWidth) / 2f;
        float plateY = (float)BarPlateTop;

        bar.Draw(canvas, plateX, plateY, scale: 1f, snapshot, DateTime.Now, elapsedMs,
                 barWidth: plateWidth);

        // Watch centrepiece sits in the alcove, centred on the plate. The
        // outer watch radius is intentionally larger than the plate's
        // half-height so the watch bleeds above and below the bar — the
        // legacy "watch overhang". Drop-shadow + entrance animation match
        // Port 01 exactly.
        float watchCx = plateX + plateWidth / 2f;
        float watchCy = plateY + plateHeight / 2f;
        float watchRadius = 64f;

        var arrive = WatchArrive.Evaluate(elapsedMs);

        float shadowDy = 3f * watchRadius / 64f;
        float shadowSigma = 4f * watchRadius / 64f;
        using var blur = arrive.Blur > 0.01f
            ? SKImageFilter.CreateBlur(arrive.Blur * watchRadius / 64f, arrive.Blur * watchRadius / 64f)
            : null;
        using var shadow = SKImageFilter.CreateDropShadow(
            0, shadowDy, shadowSigma, shadowSigma, new SKColor(0, 0, 0, 115), blur);
        using var layerPaint = new SKPaint
        {
            ImageFilter = shadow,
            Color = new SKColor(0, 0, 0, (byte)(arrive.Opacity * 255)),
        };

        canvas.SaveLayer(layerPaint);
        canvas.Translate(watchCx, watchCy);
        canvas.RotateDegrees(arrive.RotationDeg);
        canvas.Scale(arrive.Scale);
        canvas.Translate(-watchCx, -watchCy);
        watch.Draw(canvas, watchCx, watchCy, watchRadius, DateTime.Now);
        canvas.Restore();
    }

    float ComputeBarWidth(float screenWidth, double elapsedMs)
    {
        bool wantFull = PresentationState.BarFullWidth;
        if (wantFull != wasFullWidth)
        {
            widthFlipAtMs = elapsedMs;
            wasFullWidth = wantFull;
        }
        float defaultWidth = SessionBarRenderer.BarWidth;
        // When the bar is wider than the screen (unlikely but possible on a
        // narrow primary monitor), clamp to the screen so the plate edges
        // don't paint off-canvas.
        float fullWidth = Math.Max(defaultWidth, screenWidth);
        if (widthFlipAtMs < 0) return wantFull ? fullWidth : defaultWidth;
        double t = (elapsedMs - widthFlipAtMs) / BarExpandAnimMs;
        float prog = (float)BroadcastTiming.Smoothstep(t);
        return wantFull
            ? defaultWidth + (fullWidth - defaultWidth) * prog
            : fullWidth - (fullWidth - defaultWidth) * prog;
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        watch.Dispose();
        bar.Dispose();
        base.OnClosed(e);
    }
}
