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
/// Port 08 — Pit Callout window. Transparent, click-through, topmost,
/// no-taskbar, centred horizontally on the primary monitor and pinned near
/// the top (the legacy slot at y=164 on 1080 = 15.2% down). Reuses the
/// click-through mechanics established in every prior port.
/// </summary>
public partial class PitCalloutWindow : Window
{
    const int GwlExStyle = -20;
    const int WsExTransparent = 0x00000020;
    const int WsExLayered = 0x00080000;

    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    // Legacy window 784×144 with 12 px body padding around a 760×120 card.
    const double WindowWidth = 784;
    const double WindowHeight = 144;
    const double CardOffsetX = (WindowWidth - PitCalloutRenderer.CardWidth) / 2.0;
    const double CardOffsetY = 12;

    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly PitCalloutRenderer renderer = new();

    public PitCalloutWindow()
    {
        InitializeComponent();

        Width = WindowWidth;
        Height = WindowHeight;
        Left = (SystemParameters.PrimaryScreenWidth - WindowWidth) / 2.0;
        // Legacy y=164 on a 1080-tall primary monitor (15.2% down) — keep that.
        Top = SystemParameters.PrimaryScreenHeight * 0.152;

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
