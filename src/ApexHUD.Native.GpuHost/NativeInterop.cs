using System.Runtime.InteropServices;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// Win32 + WGL + minimal OpenGL P/Invoke surface for the native GPU overlay
/// host. Deliberately small — only the calls the host needs to create a
/// per-pixel-alpha, click-through, topmost, GPU-composited window, bind a WGL
/// GL context, and present each frame. The transparency technique (layered
/// window + LWA_ALPHA 255 + DWM extend-frame "sheet of glass" + an alpha pixel
/// format) is the one validated by the Session-26 POC / OverlaySharp (MIT).
/// </summary>
internal static class NativeInterop
{
    // ----- window styles -----
    public const int GwlExStyle = -20;

    public const uint WsPopup   = 0x80000000;
    public const uint WsVisible = 0x10000000;

    public const uint WsExLayered    = 0x00080000;
    public const uint WsExTransparent = 0x00000020;
    public const uint WsExTopmost    = 0x00000008;
    public const uint WsExNoActivate = 0x08000000;
    public const uint WsExToolWindow = 0x00000080; // keep the overlay off the taskbar / alt-tab

    public const uint LwaAlpha = 0x00000002;

    // ----- window messages -----
    public const uint WmDestroy   = 0x0002;
    public const uint WmClose     = 0x0010;
    public const uint WmPaint     = 0x000F;
    public const uint WmEraseBkgnd = 0x0014;
    public const uint WmNcPaint   = 0x0085;
    public const uint WmNcHitTest = 0x0084;
    public const int  HtTransparent = -1;
    public const uint WmQuit      = 0x0012;

    public const uint PmRemove = 0x0001;

    // SetWindowPos flags
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpNoZOrder   = 0x0004;
    public const uint SwpShowWindow = 0x0040;
    public static readonly IntPtr HwndTopmost = new(-1);

    // ----- pixel format flags -----
    public const uint PfdDrawToWindow = 0x00000004;
    public const uint PfdSupportOpenGl = 0x00000020;
    public const uint PfdDoubleBuffer  = 0x00000001;
    public const byte PfdTypeRgba = 0;
    public const byte PfdMainPlane = 0;

    // ----- GL constants -----
    public const uint GlColorBufferBit   = 0x00004000;
    public const uint GlBlend            = 0x0BE2;
    public const uint GlSrcAlpha         = 0x0302;
    public const uint GlOneMinusSrcAlpha = 0x0303;
    public const uint GlFramebufferBinding = 0x8CA6;
    public const uint GlRgba8            = 0x8058;

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize;
        public ushort nVersion;
        public uint dwFlags;
        public byte iPixelType;
        public byte cColorBits;
        public byte cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift;
        public byte cAlphaBits, cAlphaShift;
        public byte cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits;
        public byte cDepthBits;
        public byte cStencilBits;
        public byte cAuxBuffers;
        public byte iLayerType;
        public byte bReserved;
        public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    // ----- user32 -----
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool UnregisterClass(string className, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int index, int newLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool ValidateRect(IntPtr hWnd, IntPtr rect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    // ----- kernel32 -----
    [DllImport("kernel32.dll")]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    // ----- gdi32 -----
    [DllImport("gdi32.dll")]
    public static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR pfd);

    [DllImport("gdi32.dll")]
    public static extern bool SetPixelFormat(IntPtr hdc, int format, ref PIXELFORMATDESCRIPTOR pfd);

    [DllImport("gdi32.dll")]
    public static extern int DescribePixelFormat(IntPtr hdc, int iPixelFormat, uint nBytes, ref PIXELFORMATDESCRIPTOR ppfd);

    [DllImport("gdi32.dll")]
    public static extern bool SwapBuffers(IntPtr hdc);

    // ----- dwmapi -----
    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS margins);

    // Per-pixel alpha for a non-layered GL window (the GLFW method): DwmEnableBlurBehindWindow
    // with an EMPTY blur region enables alpha compositing with no actual blur.
    public const uint DwmBbEnable = 0x1;
    public const uint DwmBbBlurRegion = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        public bool fEnable;
        public IntPtr hRgnBlur;
        public bool fTransitionOnMaximized;
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmEnableBlurBehindWindow(IntPtr hWnd, ref DWM_BLURBEHIND bb);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    // ----- opengl32 (WGL + core GL we touch) -----
    [DllImport("opengl32.dll")]
    public static extern IntPtr wglCreateContext(IntPtr hdc);

    [DllImport("opengl32.dll")]
    public static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);

    [DllImport("opengl32.dll")]
    public static extern bool wglDeleteContext(IntPtr hglrc);

    [DllImport("opengl32.dll")]
    public static extern IntPtr wglGetProcAddress(string name);

    [DllImport("opengl32.dll", EntryPoint = "glClear")]
    public static extern void glClear(uint mask);

    [DllImport("opengl32.dll", EntryPoint = "glClearColor")]
    public static extern void glClearColor(float r, float g, float b, float a);

    [DllImport("opengl32.dll", EntryPoint = "glViewport")]
    public static extern void glViewport(int x, int y, int width, int height);

    [DllImport("opengl32.dll", EntryPoint = "glEnable")]
    public static extern void glEnable(uint cap);

    [DllImport("opengl32.dll", EntryPoint = "glBlendFunc")]
    public static extern void glBlendFunc(uint sfactor, uint dfactor);

    [DllImport("opengl32.dll", EntryPoint = "glGetIntegerv")]
    public static extern void glGetIntegerv(uint pname, out int data);

    [DllImport("opengl32.dll", EntryPoint = "glFlush")]
    public static extern void glFlush();

    [DllImport("opengl32.dll", EntryPoint = "glFinish")]
    public static extern void glFinish();

    public const uint GlVendor = 0x1F00, GlRenderer = 0x1F01, GlVersion = 0x1F02;

    [DllImport("opengl32.dll", EntryPoint = "glGetString")]
    public static extern IntPtr glGetString(uint name);

    // wglSwapIntervalEXT is an extension — resolved at runtime via wglGetProcAddress.
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int WglSwapIntervalExt(int interval);
}
