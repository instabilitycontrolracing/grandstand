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
/// Port 08 — Fastest Lap callout window. Transparent, click-through, topmost,
/// no-taskbar, centred horizontally on the primary monitor and placed where
/// the legacy slot sat (≈ 49 % down the screen). Reuses the click-through
/// mechanics established Phase 2 / Port 01 / 03 / 04 / 05 / 06 / 07.
/// </summary>
public partial class FastestLapWindow : Window
{
    const int GwlExStyle = -20;
    const int WsExTransparent = 0x00000020;
    const int WsExLayered = 0x00080000;

    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    // Window slot mirrors the legacy 784×174 with 16 px body padding and the
    // card aligned to the bottom of the window (body display:flex align-end).
    // Card itself is 720×110 sitting 32 px from the left and at the bottom.
    const double WindowWidth = 784;
    const double WindowHeight = 174;
    const double CardOffsetX = (WindowWidth - FastestLapRenderer.CardWidth) / 2.0;
    const double CardOffsetY = WindowHeight - FastestLapRenderer.CardHeight - 16;

    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly FastestLapRenderer renderer = new();

    public FastestLapWindow()
    {
        InitializeComponent();

        Width = WindowWidth;
        Height = WindowHeight;
        Left = (SystemParameters.PrimaryScreenWidth - WindowWidth) / 2.0;
        // Legacy placement on a 1920×1080 monitor was (568, 532) — 49.3% down.
        // Translate to primary-monitor-relative so it adapts to Angelo's rig.
        Top = SystemParameters.PrimaryScreenHeight * 0.493;
        if (Top + WindowHeight > SystemParameters.PrimaryScreenHeight)
            Top = SystemParameters.PrimaryScreenHeight - WindowHeight - 8;

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
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        renderer.Dispose();
        base.OnClosed(e);
    }
}
