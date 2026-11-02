using System.Runtime.InteropServices;
using SkiaSharp;

/// <summary>Headless OpenGL for the bench (Mesa llvmpipe through EGL, no window): lets Skia's GPU backend run the
/// same code path as the PC (GRContext.CreateGl), so the CPU work Skia does to feed the graphics card - software path
/// masks, blurred glyphs, layer set-up - shows up in the timings. The GPU itself is emulated (llvmpipe), so only the
/// "cpu" column is meaningful for the PC.</summary>
static class Egl
{
    const string Lib = "libEGL.so.1";
    [DllImport(Lib)] static extern IntPtr eglGetProcAddress(string name);
    [DllImport(Lib)] static extern IntPtr eglGetDisplay(IntPtr id);
    [DllImport(Lib)] static extern bool eglInitialize(IntPtr d, out int major, out int minor);
    [DllImport(Lib)] static extern bool eglBindAPI(int api);
    [DllImport(Lib)] static extern bool eglChooseConfig(IntPtr d, int[] attribs, IntPtr[] configs, int size, out int n);
    [DllImport(Lib)] static extern IntPtr eglCreatePbufferSurface(IntPtr d, IntPtr cfg, int[] attribs);
    [DllImport(Lib)] static extern IntPtr eglCreateContext(IntPtr d, IntPtr cfg, IntPtr share, int[] attribs);
    [DllImport(Lib)] static extern bool eglMakeCurrent(IntPtr d, IntPtr draw, IntPtr read, IntPtr ctx);
    [DllImport(Lib)] static extern int eglGetError();
    delegate IntPtr GetPlatformDisplay(int platform, IntPtr native, int[]? attribs);
    delegate void GlFinish();
    static GlFinish? finish;

    public static GRContext? Create()
    {
        IntPtr d = IntPtr.Zero;
        var gpd = eglGetProcAddress("eglGetPlatformDisplayEXT");
        if (gpd != IntPtr.Zero)
            d = Marshal.GetDelegateForFunctionPointer<GetPlatformDisplay>(gpd)(0x31DD /* EGL_PLATFORM_SURFACELESS_MESA */, IntPtr.Zero, null);
        if (d == IntPtr.Zero) d = eglGetDisplay(IntPtr.Zero);
        if (!eglInitialize(d, out _, out _)) { Console.WriteLine($"eglInitialize failed 0x{eglGetError():X}"); return null; }
        if (!eglBindAPI(0x30A2 /* EGL_OPENGL_API */)) { Console.WriteLine("eglBindAPI failed"); return null; }
        var cfgs = new IntPtr[1];
        int[] attr = { 0x3033, 0x0001 /* SURFACE_TYPE PBUFFER */, 0x3040, 0x0008 /* RENDERABLE OPENGL_BIT */,
                       0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8, 0x3026, 8 /* stencil */, 0x3038 };
        if (!eglChooseConfig(d, attr, cfgs, 1, out int n) || n == 0) { Console.WriteLine($"eglChooseConfig failed 0x{eglGetError():X}"); return null; }
        var surf = eglCreatePbufferSurface(d, cfgs[0], new[] { 0x3057, 16, 0x3056, 16, 0x3038 });
        var ctx = eglCreateContext(d, cfgs[0], IntPtr.Zero, new[] { 0x3038 });
        if (ctx == IntPtr.Zero || !eglMakeCurrent(d, surf, surf, ctx)) { Console.WriteLine($"eglMakeCurrent failed 0x{eglGetError():X}"); return null; }
        var fin = eglGetProcAddress("glFinish");
        if (fin != IntPtr.Zero) finish = Marshal.GetDelegateForFunctionPointer<GlFinish>(fin);
        var gl = GRGlInterface.Create(name => eglGetProcAddress(name));
        if (gl == null) { Console.WriteLine("GRGlInterface.Create returned null"); return null; }
        return GRContext.CreateGl(gl);
    }

    public static void Finish() => finish?.Invoke();
}
