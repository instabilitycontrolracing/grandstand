using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using static ApexHUD.Native.GpuHost.NativeInterop;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// A native, GPU-composited, per-pixel-alpha, click-through, topmost overlay
/// window that owns its own render thread (independent of any WPF dispatcher)
/// and hands a raw <see cref="SKCanvas"/> to a draw callback each vsync.
///
/// <para>The window, the WGL GL context, and every SkiaSharp GPU object are
/// created, used, and disposed on the single render thread, so there is no
/// cross-thread GL state and teardown is fully in our control — the
/// <see cref="Stop"/> hang the Session-26 POC hit (OverlaySharp owned the loop)
/// cannot happen here.</para>
///
/// <para>Transparency uses an alpha (cAlphaBits=8) pixel format plus DWM "blur
/// behind" over an EMPTY region (the GLFW method), which gives per-pixel alpha
/// compositing with no actual blur. The window is WS_EX_LAYERED + WS_EX_TRANSPARENT
/// for cross-process click-through (clicks fall through to iRacing), with
/// SetLayeredWindowAttributes(LWA_ALPHA 255) to keep it visible -- layering does NOT
/// break GL compositing once the framebuffer is correct. Two
/// further details are essential: Skia must wrap the window's on-screen framebuffer
/// (FBO 0), not whatever transient FBO the context left bound; and the window must
/// NOT cover the whole screen (it leaves a small gap), otherwise Windows promotes it
/// to DirectFlip/independent-flip, bypasses the compositor, and ignores alpha
/// (transparent areas show as black). GL clears to a fully transparent buffer and
/// blends premultiplied, so wherever the renderers leave the canvas transparent the
/// desktop / iRacing shows through, composited by the GPU.</para>
/// </summary>
internal sealed class NativeOverlayHost : IDisposable
{
    const string ClassName = "ApexHudGpuHostWindow";
    const uint CsOwnDc = 0x0020, CsHRedraw = 0x0002, CsVRedraw = 0x0001;
    // Quit hotkey: Ctrl+Shift+Q held together (polled globally). Esc is deliberately
    // NOT used — iRacing uses Esc in-sim and the overlay must never interfere with it.
    const int VkControl = 0x11, VkShift = 0x10, VkQ = 0x51;

    // The window deliberately leaves a small gap so it does NOT cover the whole
    // screen. A borderless window that covers the entire screen is promoted to
    // DirectFlip / independent-flip, which bypasses the desktop compositor and
    // ignores per-pixel alpha -> transparent areas show as opaque black. Leaving the
    // screen not-fully-covered forces DWM to composite, honoring our alpha. Layout
    // still uses the full logical size; the gap sits below all overlay content.
    const int EdgePad = 1;   // was 8 (Angelo 2026-10-23: a strip of track showed under the intro); 1 px is enough to keep DWM compositing

    // How long the render thread sleeps when the on-demand decision says nothing
    // changed. Short enough that the first burst frame and an Esc press are seen
    // within a few ms; long enough that idle polling costs negligible CPU.
    const int IdleSleepMs = 8;
    // 2026-10-29 (Angelo: "look at the idle cost"): with no iRacing session the main window has nothing to react to
    // at 8 ms, and the hidden TV splash / review window spends the whole race hidden - both now poll at 40 / 50 ms
    // (the state pass ran 125 times a second for nothing: ~17% of a core while waiting for the sim).
    const int DeepIdleSleepMs = 40, HiddenSleepMs = 50;
    /// <summary>Set by the compositor every tick: no iRacing session and no demo - nothing live to react to.</summary>
    public static volatile bool DeepIdle;

    // Burst rate cap. Even while something is animating we present at most ~60 fps
    // regardless of the display's refresh — broadcast overlays need smooth motion,
    // not 144/240 Hz, and pinning the GPU to the panel's refresh is exactly the
    // "drive the display refresh" anti-pattern GPU_FINDINGS warns against (kind to
    // iRacing's frame budget). Slightly under 1/60 s so a true 60 Hz vsync is never
    // throttled (its ~16.67 ms always clears 15.5 ms). A 30 fps clip then shows
    // each frame for exactly two presents.
    const double TargetPresentIntervalMs = 16.6;   // ~60 fps; VSync is off now (see swap(0)), so this is the pacing
    const int BurstNapMs = 2;        // short nap while bursting but not yet due to present

    readonly Action<SKCanvas, int, int, double> render;
    // On-demand gate: returns true when this frame must be presented. Null => the
    // host renders every loop iteration (Phase B behaviour).
    readonly Func<double, bool>? frameNeeded;
    // Invoked once on the render thread the moment the GPU context is ready, so
    // callers can validate GPU-side seams (e.g. the frame-upload path) on the
    // real GL context.
    readonly Action<GRContext>? onGpuReady;

    Thread? thread;
    volatile bool running;
    volatile bool forceRender;          // forces one present (startup + after a resize)
    double lastPresentMs = -1e9;        // render-thread-only; gates the burst rate cap

    // Window / GL state — all touched only on the render thread.
    IntPtr hwnd, hdc, glrc;
    WndProc? wndProcDelegate; // kept alive against GC for the window's lifetime
    IntPtr hInstance;

    GRContext? grContext;
    GRGlInterface? glInterface;
    GRBackendRenderTarget? renderTarget;
    SKSurface? surface;

    // Desired window bounds — written from any thread (window targeting), applied
    // at the top of each render frame.
    readonly object boundsLock = new();
    int wantX, wantY, wantW, wantH;
    int curX = int.MinValue, curY = int.MinValue, curW, curH;

    // Presented-frame cadence, recorded on the render thread, read under lock.
    readonly object gapsLock = new();
    readonly List<double> gaps = new(8192);
    static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
    long lastPresentTs;

    // Wall clock for the render thread; the single time source handed to both the
    // on-demand decision and the draw callback so they agree frame to frame.
    readonly Stopwatch renderClock = new();

    /// <summary>Total frames actually presented (SwapBuffers) so far. Read from
    /// any thread to compute presents-per-second (proves the on-demand idle).</summary>
    public long PresentedFrames => Interlocked.Read(ref frameCount);

    /// <summary>True until the render thread exits (Esc, target loss, or Stop).</summary>
    public bool IsRunning => running;

    /// <summary>Set true by the render thread when the quit hotkey (Ctrl+Shift+Q)
    /// is detected, so the owner can break its own loop and tear down.</summary>
    public volatile bool QuitRequested;

    // 2026-10-27 (loading splash): a second host instance draws the TV splash. It needs its own window class,
    // starts hidden, has its own present cap, and leaves the main overlay's global duties alone (quit hotkey,
    // Spout, the opener's frame watch, auto light mode).
    readonly string className = ClassName, windowTitle = "Grandstand", logTag = "";
    readonly bool isMain = true;
    readonly Func<double>? presentIntervalMs;

    public NativeOverlayHost(int x, int y, int w, int h,
        Action<SKCanvas, int, int, double> render, Func<double, bool>? frameNeeded = null,
        Action<GRContext>? onGpuReady = null)
    {
        this.render = render;
        this.frameNeeded = frameNeeded;
        this.onGpuReady = onGpuReady;
        forceRender = true;             // always present the first frame
        wantX = x; wantY = y; wantW = Math.Max(16, w); wantH = Math.Max(16, h);
    }

    /// <summary>A secondary host (2026-10-27, the TV loading splash): own window class and title, created
    /// hidden, presents at most once per <paramref name="presentIntervalMs"/>, and never touches the main
    /// overlay's quit hotkey, Spout output, opener frame watch or automatic light mode.</summary>
    public NativeOverlayHost(int x, int y, int w, int h,
        Action<SKCanvas, int, int, double> render, Func<double, bool> frameNeeded,
        Action<GRContext>? onGpuReady, string className, string windowTitle, Func<double> presentIntervalMs)
        : this(x, y, w, h, render, frameNeeded, onGpuReady)
    {
        this.className = className;
        this.windowTitle = windowTitle;
        this.presentIntervalMs = presentIntervalMs;
        isMain = false;
        logTag = windowTitle + ": ";
        wantVisible = false; curVisible = false;   // created without WS_VISIBLE; shown when it has something to draw
    }

    /// <summary>Request a new on-screen position/size (window targeting). Applied
    /// on the next frame; resizes recreate the GPU surface.</summary>
    public void SetBounds(int x, int y, int w, int h)
    {
        lock (boundsLock) { wantX = x; wantY = y; wantW = Math.Max(16, w); wantH = Math.Max(16, h); }
    }

    // ---- Auto show/hide with the sim (2026-10-18) ----------------------------
    // The owner (Program) calls SetVisible(false) when iRacing's sim window is
    // gone and SetVisible(true) when it appears, so the overlay behaves like
    // CrewChief: always running, only on screen while the sim is up. Applied on
    // the render thread (the thread that owns the window).
    volatile bool wantVisible = true;
    bool curVisible = true;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    const int SwHide = 0, SwShowNoActivate = 4;

    /// <summary>Show or hide the overlay window (applied on the next frame).</summary>
    public void SetVisible(bool visible) => wantVisible = visible;

    void ApplyVisibility()
    {
        bool want = wantVisible;
        if (want == curVisible || hwnd == IntPtr.Zero) return;
        ShowWindow(hwnd, want ? SwShowNoActivate : SwHide);
        // the splash goes over everything else in the topmost band (iRacing UI, the main overlay) when it appears
        if (want && !isMain) SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoActivate | 0x0001u /*NOSIZE*/ | 0x0002u /*NOMOVE*/);
        curVisible = want;
        if (want) { forceRender = true; blankSwaps = 0; }   // repaint the moment it comes back
    }

    public void Start()
    {
        running = true;
        thread = new Thread(RenderThreadMain) { Name = "ApexHudGpuHostRender", IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public void Stop()
    {
        running = false;
        thread?.Join(TimeSpan.FromSeconds(3));
        thread = null;
    }

    public void Dispose() => Stop();

    /// <summary>Snapshot of presented-frame gaps (ms) recorded so far.</summary>
    public double[] CopyGaps()
    {
        lock (gapsLock) return gaps.ToArray();
    }

    void RenderThreadMain()
    {
        try
        {
            CreateWindowAndContext();
            CreateSurface(curW, curH - EdgePad);
            renderClock.Start();

            var msgPump = new Action(PumpMessages);
            if (!isMain) Program.Log(logTag + "render loop starting");
            int dbg = 0;

            while (running)
            {
                if (!isMain && dbg < 3) Program.Log(logTag + "loop: pump");
                msgPump();
                if (!isMain && dbg < 3) Program.Log(logTag + "loop: pumped");
                if (!running) break;

                if (!isMain && dbg < 3) Program.Log(logTag + "loop: bounds");
                ApplyPendingBounds();
                ApplyVisibility();
                if (!isMain && dbg < 3) { Program.Log(logTag + "loop: tick"); dbg++; }

                if (isMain && (GetAsyncKeyState(VkControl) & 0x8000) != 0
                    && (GetAsyncKeyState(VkShift) & 0x8000) != 0
                    && (GetAsyncKeyState(VkQ) & 0x8000) != 0
                    && QuitKeyAllowedHere())
                {
                    QuitRequested = true;
                    running = false;
                    break;
                }

                double nowMs = renderClock.Elapsed.TotalMilliseconds;
                if (!isMain && !curVisible) PurgeWhileHidden(nowMs);

                // Always evaluate the decision — it advances the shared
                // visibility/rotation statics and sets each overlay's paint flag,
                // which the draw pass relies on, even on a forced present.
                bool need = frameNeeded?.Invoke(nowMs) ?? true;

                // On-demand present, capped to the burst rate: render + SwapBuffers
                // ONLY when something changed AND we are due for the next ~60 fps
                // present (or a forced present after startup/resize). Otherwise idle
                // the thread — a static overlay costs ~0 GPU, and the double-buffered
                // front buffer keeps the last composite on screen.
                double interval = presentIntervalMs?.Invoke() ?? ApexHUD.Native.Overlays.Perf.PresentIntervalMs;
                if (isMain && DeepIdle) interval = Math.Max(interval, 1000.0 / 20);   // no session: the stand-by graphics at 20 fps
                bool due = (nowMs - lastPresentMs) >= interval;   // 60 fps, 30 in light mode (the splash sets its own)
                // hidden (no live session): keep ticking the state above, but draw nothing (2026-10-24:
                // it was drawing ~56 frames a second into a hidden window)
                if (forceRender || (need && due && curVisible))
                {
                    forceRender = false;
                    lastPresentMs = nowMs;
                    RenderOneFrame(nowMs);
                }
                else
                {
                    // While bursting but not yet due, nap briefly to hit the cadence;
                    // while idle, nap longer (just polling for the next change).
                    // 2026-10-29 (efficiency): while bursting, the first nap after a present now sleeps through most of
                    // the wait (up to IdleSleepMs) and only the last ~2 ms before the next present are polled in
                    // BurstNapMs steps - the present lands on the same 2 ms grid as before, but the full state pass
                    // (FrameNeeded, ~45 us) runs ~4x per presented frame instead of ~8x. Hidden: nothing is drawn,
                    // so the state pass runs at the idle cadence.
                    int nap = IdleSleepMs;
                    if (!isMain && !curVisible) nap = HiddenSleepMs;
                    else if (isMain && DeepIdle && !(need && curVisible)) nap = DeepIdleSleepMs;
                    if (need && curVisible)
                    {
                        double left = interval - (nowMs - lastPresentMs);
                        nap = (int)Math.Clamp(left - BurstNapMs, BurstNapMs, IdleSleepMs);
                    }
                    Thread.Sleep(nap);
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogFatal($"render thread: {ex}");
        }
        finally
        {
            running = false;
            TeardownContextAndWindow();
        }
    }

    // 2026-10-29 (efficiency, GPU memory): the TV splash / session review window spends the whole race hidden, but its
    // own Skia context kept every scratch texture, layer and glyph atlas it had used for the menus. Every 20 s while
    // hidden, anything it has not touched for a minute is handed back to the card (its pictures stay - they are in use).
    double hiddenPurgeMs = double.NegativeInfinity;
    void PurgeWhileHidden(double nowMs)
    {
        if (nowMs - hiddenPurgeMs < 20000 || grContext == null) return;
        hiddenPurgeMs = nowMs;
        try
        {
            grContext.GetResourceCacheUsage(out _, out long before);
            grContext.PurgeUnusedResources(60_000);
            grContext.GetResourceCacheUsage(out _, out long after);
            if (before - after > 8L * 1024 * 1024)
                Program.Log($"{logTag}hidden - graphics memory {before / (1024 * 1024)} -> {after / (1024 * 1024)} MB");
        }
        catch (Exception ex) { Program.Log($"{logTag}purge: {ex.Message}"); }
    }

    bool pumpWarned;
    void PumpMessages()
    {
        // 2026-10-27: at most 64 messages per loop - the TV splash's hidden window got a message that kept
        // coming back, and an unbounded drain froze its render loop (the splash never appeared)
        int n = 0;
        while (n++ < 64 && PeekMessage(out var msg, isMain ? IntPtr.Zero : hwnd, 0, 0, PmRemove))
        {
            if (!isMain && n == 64 && !pumpWarned) { pumpWarned = true; Program.Log($"{logTag}message flood: 0x{msg.message:X} hwnd=0x{msg.hwnd:X}"); }
            if (msg.message == WmQuit) { running = false; return; }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    void ApplyPendingBounds()
    {
        int x, y, w, h;
        lock (boundsLock) { x = wantX; y = wantY; w = wantW; h = wantH; }
        if (x == curX && y == curY && w == curW && h == curH) return;

        bool sizeChanged = w != curW || h != curH;
        // Only force-show on reposition when the overlay is meant to be visible
        // (it stays hidden while the sim is closed - see ApplyVisibility).
        SetWindowPos(hwnd, HwndTopmost, x, y, w, h - EdgePad, SwpNoActivate | (curVisible ? SwpShowWindow : 0u));
        curX = x; curY = y; curW = w; curH = h;

        if (sizeChanged)
        {
            glViewport(0, 0, w, h - EdgePad);
            CreateSurface(w, h - EdgePad);
            forceRender = true;         // the recreated back buffer is blank — repaint once
            blankSwaps = 0;
        }
    }

    long frameCount;
    double costDraw, costFlush, costMax, costSinceMs; int costN;
    /// <summary>Per-graphic cost breakdown for the frame-cost log line (set by Program to the compositor's).</summary>
    public static Func<string>? CostDetail;

    static bool SpoutWasOn;
    int blankSwaps;   // see-through "stream only" frames swapped in a row (render thread)

    void RenderOneFrame(double nowMs)
    {
        var canvas = surface?.Canvas;
        if (canvas == null) return;

        bool first = frameCount == 0;
        if (first) Program.Log(logTag + "frame0: pre-render");

        // 2026-10-29 (efficiency): no raw glClear before Skia's own clear any more - canvas.Clear(Transparent) below
        // already clears the whole back buffer (the surface is the full window), so the 4K buffer was cleared twice
        long t0 = Stopwatch.GetTimestamp();
        canvas.Clear(SKColors.Transparent);
        render(canvas, curW, curH, nowMs);
        long t1 = Stopwatch.GetTimestamp();
        canvas.Flush();
        grContext?.Flush();
        long t2 = Stopwatch.GetTimestamp();
        // frame cost (2026-10-24): draw (CPU) and flush (GPU submit) per 10 s window, so a choppy
        // patch in the log shows whether the overlay itself got heavy or the GPU was busy elsewhere
        costDraw += (t1 - t0) * MsPerTick; costFlush += (t2 - t1) * MsPerTick; costN++;
        if (isMain) { try { IntroWatch.Frame(nowMs, (t1 - t0) * MsPerTick, (t2 - t1) * MsPerTick, OverlayCompositor.LastGridMs); } catch { } }
        costMax = Math.Max(costMax, (t2 - t0) * MsPerTick);
        if (!isMain && nowMs - costSinceMs > 10000)
        {
            if (costN > 30) Program.Log($"{logTag}frame cost: {costN} frames, draw {costDraw / costN:F1} ms + flush {costFlush / costN:F1} ms avg, worst {costMax:F0} ms, {curW}x{curH}");
            costDraw = costFlush = costMax = 0; costN = 0; costSinceMs = nowMs;
        }
        if (isMain && nowMs - costSinceMs > 10000)
        {
            string detail = "";
            try { detail = CostDetail?.Invoke() ?? ""; } catch { }
            if (costN > 30) Program.Log($"frame cost: {costN} frames, draw {costDraw / costN:F1} ms + flush {costFlush / costN:F1} ms avg, worst {costMax:F0} ms, {curW}x{curH} | {detail}");
            if (costN > 30 && ApexHUD.Native.Overlays.Perf.ReportFrameCost((costDraw + costFlush) / costN))
                Program.Log("performance: frames are heavy on this PC - light mode switched on automatically (30 fps, less decorative motion)");
            costDraw = costFlush = costMax = 0; costN = 0; costSinceMs = nowMs;
        }

        // 2026-10-26 (#30): Spout2 stream output for OBS - off unless switched on in the control app
        bool spoutOn = isMain && ControlState.Current.Spout;
        bool blank = false;
        if (isMain && (spoutOn || SpoutWasOn))
        {
            SpoutOut.Frame(spoutOn, curW, curH);
            SpoutWasOn = spoutOn;
            if (spoutOn) grContext?.ResetContext();   // Spout used GL behind Skia's back
            // "stream only": OBS got the frame - the driver's own screen gets an empty (see-through) one
            if (spoutOn && ControlState.Current.StreamOnly)
            {
                glClearColor(0f, 0f, 0f, 0f);
                glClear(GlColorBufferBit);
                grContext?.ResetContext();
                blank = true;
            }
        }

        if (first) Program.Log(logTag + "frame0: pre-swap");
        // 2026-10-29 (efficiency): in "stream only" every present is the same see-through frame. Once both buffers of
        // the window have shown it, swapping again changes nothing on screen - it only made Windows composite a 4K
        // window ~60 times a second. Spout has already taken the real frame above, so the swap is skipped then. Any
        // normal frame, a resize or the window coming back swaps again straight away.
        if (blank && blankSwaps >= 2) { }
        else SwapBuffers(hdc);
        blankSwaps = blank ? Math.Min(blankSwaps + 1, 2) : 0;
        if (first) Program.Log(logTag + "frame0: post-swap");
        frameCount++;

        long now = Stopwatch.GetTimestamp();
        if (lastPresentTs != 0)
        {
            double gap = (now - lastPresentTs) * MsPerTick;
            lock (gapsLock)
            {
                if (gaps.Count >= 30000) gaps.RemoveRange(0, 10000);   // keep recent history; never grow for hours
                gaps.Add(gap);
            }
        }
        lastPresentTs = now;
    }

    /// <summary>Ctrl+Shift+Q only quits while iRacing (or this overlay) is the window in front,
    /// so typing that combination in another program never kills the broadcast.</summary>
    bool QuitKeyAllowedHere()
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == hwnd) return true;
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(fg, sb, sb.Capacity);
            return sb.ToString().StartsWith("iRacing.com Simulator", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    void CreateWindowAndContext()
    {
        hInstance = GetModuleHandle(null);
        wndProcDelegate = StaticWndProc;

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = CsOwnDc | CsHRedraw | CsVRedraw,
            lpfnWndProc = wndProcDelegate,
            hInstance = hInstance,
            lpszClassName = className,
        };
        if (RegisterClassEx(ref wc) == 0)
            throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastWin32Error()})");

        // WS_EX_LAYERED + WS_EX_TRANSPARENT together are REQUIRED for cross-process
        // click-through (clicks falling through to iRacing / the desktop). WsExTransparent
        // on its own, or a WM_NCHITTEST HTTRANSPARENT handler, only passes clicks between
        // windows of the SAME thread -- not to other apps. Contrary to an earlier belief,
        // WS_EX_LAYERED does NOT break GL compositing here: per-pixel alpha still comes
        // from the alpha pixel format + DWM blur-behind below, and SetLayeredWindowAttributes
        // (LWA_ALPHA 255) makes the layered window visible. (The earlier "layered =
        // invisible" symptom was actually the wrong-framebuffer bug, since fixed.)
        uint exStyle = WsExLayered | WsExTransparent | WsExTopmost | WsExNoActivate | WsExToolWindow;
        hwnd = CreateWindowEx(exStyle, className, windowTitle, WsPopup | (curVisible ? WsVisible : 0u),
            wantX, wantY, wantW, wantH - EdgePad, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()})");

        curX = wantX; curY = wantY; curW = wantW; curH = wantH;

        // WS_EX_LAYERED + WS_EX_TRANSPARENT is the Windows recipe for true cross-process
        // click-through (clicks fall through to iRacing / the desktop). A layered window
        // must call SetLayeredWindowAttributes at least once to be shown; LWA_ALPHA 255 is
        // a fully-opaque baseline, with per-pixel transparency still coming from the GL
        // buffer's alpha composited by DWM (blur-behind below).
        SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);

        // Per-pixel alpha for a NON-layered GL window: enable DWM "blur behind" over
        // an EMPTY region -> per-pixel alpha compositing of the GL framebuffer alpha with
        // NO actual blur. This is the method GLFW uses for transparent framebuffers and
        // the one that works on Windows 11 (DwmExtendFrameIntoClientArea composited the
        // client area as opaque black here).
        IntPtr emptyRegion = CreateRectRgn(0, 0, -1, -1);
        var blurBehind = new DWM_BLURBEHIND { dwFlags = DwmBbEnable | DwmBbBlurRegion, fEnable = true, hRgnBlur = emptyRegion, fTransitionOnMaximized = false };
        DwmEnableBlurBehindWindow(hwnd, ref blurBehind);
        DeleteObject(emptyRegion);

        hdc = GetDC(hwnd);
        if (hdc == IntPtr.Zero) throw new InvalidOperationException("GetDC failed");

        var pfd = new PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(),
            nVersion = 1,
            dwFlags = PfdDrawToWindow | PfdSupportOpenGl | PfdDoubleBuffer,
            iPixelType = PfdTypeRgba,
            cColorBits = 32,
            cAlphaBits = 8,
            cDepthBits = 24,
            cStencilBits = 8,
            iLayerType = PfdMainPlane,
        };
        // ChoosePixelFormat frequently returns a format with NO destination alpha,
        // which makes DWM composite the window as OPAQUE (black where we cleared to
        // transparent). Enumerate formats and prefer one that has cAlphaBits >= 8.
        int max = DescribePixelFormat(hdc, 1, (uint)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(), ref pfd);
        int format = 0;
        for (int i = 1; i <= max; i++)
        {
            var t = new PIXELFORMATDESCRIPTOR { nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(), nVersion = 1 };
            if (DescribePixelFormat(hdc, i, (uint)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(), ref t) == 0) continue;
            bool ok = (t.dwFlags & PfdDrawToWindow) != 0 && (t.dwFlags & PfdSupportOpenGl) != 0
                      && (t.dwFlags & PfdDoubleBuffer) != 0 && t.iPixelType == PfdTypeRgba
                      && t.cColorBits == 32 && t.cAlphaBits >= 8;
            if (ok) { format = i; break; }
        }
        if (format == 0) format = ChoosePixelFormat(hdc, ref pfd);
        if (format == 0) throw new InvalidOperationException($"ChoosePixelFormat failed ({Marshal.GetLastWin32Error()})");
        var chosenPfd = new PIXELFORMATDESCRIPTOR { nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(), nVersion = 1 };
        DescribePixelFormat(hdc, format, (uint)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(), ref chosenPfd);
        Program.Log($"{logTag}pixel format #{format}: color={chosenPfd.cColorBits} alpha={chosenPfd.cAlphaBits} flags=0x{chosenPfd.dwFlags:X}");
        if (!SetPixelFormat(hdc, format, ref chosenPfd))
            throw new InvalidOperationException($"SetPixelFormat failed ({Marshal.GetLastWin32Error()})");

        glrc = wglCreateContext(hdc);
        if (glrc == IntPtr.Zero) throw new InvalidOperationException("wglCreateContext failed");
        if (!wglMakeCurrent(hdc, glrc)) throw new InvalidOperationException("wglMakeCurrent failed");

        // VSync on — SwapBuffers then paces the loop to the display (~60 fps),
        // which is exactly the cadence Phase B is proving.
        var swapPtr = wglGetProcAddress("wglSwapIntervalEXT");
        if (swapPtr != IntPtr.Zero)
        {
            var swap = Marshal.GetDelegateForFunctionPointer<WglSwapIntervalExt>(swapPtr);
            // VSync OFF (2026-10-24 replay/4K test): with it on, any frame that took a hair over 16.7 ms
            // waited for the NEXT vblank, so a busy GPU (VR + 4K) snapped the overlay from 60 to 30 fps
            // "out of the blue". The window is composited by Windows (no tearing), and the loop already
            // caps presents at ~60 fps (TargetPresentIntervalMs), so a slow frame now costs only its own time.
            swap(0);
        }

        glEnable(GlBlend);
        glBlendFunc(GlSrcAlpha, GlOneMinusSrcAlpha);
        glViewport(0, 0, curW, curH - EdgePad);

        Program.Log($"{logTag}GL ready: {GlStr(GlVersion)} | {GlStr(GlRenderer)}");

        glInterface = GRGlInterface.Create()
            ?? throw new InvalidOperationException("GRGlInterface.Create returned null");
        grContext = GRContext.CreateGl(glInterface)
            ?? throw new InvalidOperationException("GRContext.CreateGl returned null");
        Program.Log(logTag + "GRContext created");

        // Validate any GPU-side seams on the real context (e.g. the reused-texture
        // frame-upload path) now that GL + Skia-GPU are live.
        try { onGpuReady?.Invoke(grContext); } catch (Exception ex) { Program.Log($"onGpuReady: {ex.Message}"); }
    }

    static string GlStr(uint name)
    {
        var p = glGetString(name);
        return p == IntPtr.Zero ? "?" : (Marshal.PtrToStringAnsi(p) ?? "?");
    }

    void CreateSurface(int w, int h)
    {
        surface?.Dispose();
        renderTarget?.Dispose();
        surface = null;
        renderTarget = null;
        if (grContext == null || w <= 0 || h <= 0) return;

        glGetIntegerv(GlFramebufferBinding, out int fbo);
        // Wrap the WINDOW default framebuffer (0), NOT whatever transient FBO the GL/Skia
        // context left bound (often 1). Wrapping the wrong FBO makes Skia render into a
        // buffer that is never presented -> screen shows nothing (black).
        var fbInfo = new GRGlFramebufferInfo(0, GlRgba8);
        renderTarget = new GRBackendRenderTarget(w, h, 0, 8, fbInfo);
        surface = SKSurface.Create(grContext, renderTarget, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
        Program.Log($"{logTag}surface {(surface != null ? "OK" : "NULL")} {w}x{h} wrappedFbo=0 queried={fbo} rtValid={renderTarget.IsValid}");
    }

    void TeardownContextAndWindow()
    {
        try { surface?.Dispose(); } catch { }
        try { renderTarget?.Dispose(); } catch { }
        try { grContext?.Dispose(); } catch { }
        try { glInterface?.Dispose(); } catch { }
        surface = null; renderTarget = null; grContext = null; glInterface = null;

        try { wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); } catch { }
        if (glrc != IntPtr.Zero) { try { wglDeleteContext(glrc); } catch { } glrc = IntPtr.Zero; }
        if (hdc != IntPtr.Zero) { try { ReleaseDC(hwnd, hdc); } catch { } hdc = IntPtr.Zero; }
        if (hwnd != IntPtr.Zero) { try { DestroyWindow(hwnd); } catch { } hwnd = IntPtr.Zero; }
        try { UnregisterClass(className, hInstance); } catch { }
        wndProcDelegate = null;
    }

    static IntPtr StaticWndProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        switch (msg)
        {
            case WmNcHitTest:
                // Click-through without WS_EX_LAYERED: report every point as
                // transparent so mouse/clicks fall straight through to iRacing.
                return new IntPtr(HtTransparent);
            case WmEraseBkgnd:
                return new IntPtr(1); // we own all painting via GL
            case WmPaint:
                // Clear the update region so Windows stops re-posting WM_PAINT
                // (otherwise the message pump never drains and the render loop
                // starves). We present every frame via SwapBuffers, not WM_PAINT.
                ValidateRect(h, IntPtr.Zero);
                return IntPtr.Zero;
            case WmNcPaint:
                return IntPtr.Zero;
            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProc(h, msg, w, l);
        }
    }
}
