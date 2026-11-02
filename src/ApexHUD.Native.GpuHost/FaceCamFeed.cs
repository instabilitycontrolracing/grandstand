using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// #644 (2026-10-31, Angelo): Grandstand draws the face cam itself, so the transitions can play OVER it.
/// OBS sends only the face cam source over Spout (a "Spout Filter" called "Grandstand feed" on "Face cam (VDO.Ninja)",
/// sender "GSFaceCam"). This reads that sender on its own background thread: the sender's info block (shared memory
/// named like the sender: share handle, width, height, DXGI format) -> the shared D3D11 texture -> a small staging copy
/// -> CPU pixels, ~30 times a second. The render thread only swaps in the newest frame (no waiting on the GPU copy).
/// No-face-reveal: OBS only renders (and so only sends) the face cam while the guard has it switched on, so when the
/// guard switches it off the feed goes stale and Grandstand's camera fades out by itself.
/// D3D11 is called straight through its COM tables (documented, fixed slot order), so no extra library is needed.
/// </summary>
internal static unsafe class FaceCamFeed
{
    public const string SenderName = "GSFaceCam";
    static readonly object gate = new();
    static byte[]? latest; static int latestW, latestH; static bool latestBgra; static long latestSeq;
    static double lastFrameMs = -1e9;
    static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    static int started;
    public static string Status = "not started";

    /// <summary>Milliseconds since the last new frame (large when there is no feed).</summary>
    public static double AgeMs => clock.Elapsed.TotalMilliseconds - lastFrameMs;

    public static void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 1) return;
        var t = new Thread(Loop) { IsBackground = true, Name = "facecam feed", Priority = ThreadPriority.BelowNormal };
        t.Start();
    }

    // ---- render thread side: newest frame as an SKImage (rebuilt only when a new frame arrived)
    static SKImage? image; static long imageSeq = -1;
    public static SKImage? Image()
    {
        lock (gate)
        {
            if (latest == null) return image;
            if (latestSeq != imageSeq)
            {
                var info = new SKImageInfo(latestW, latestH, latestBgra ? SKColorType.Bgra8888 : SKColorType.Rgba8888, SKAlphaType.Opaque);
                image?.Dispose();
                image = SKImage.FromPixelCopy(info, latest, latestW * 4);
                imageSeq = latestSeq;
            }
        }
        return image;
    }

    // ---- background side
    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint numLevels,
        uint sdk, out IntPtr device, out int level, out IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    struct TexDesc { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccess, Misc; }
    [StructLayout(LayoutKind.Sequential)]
    struct Mapped { public IntPtr Data; public uint RowPitch, DepthPitch; }

    static IntPtr V(IntPtr obj, int slot) => (*(IntPtr**)obj)[slot];
    static void Release(ref IntPtr p) { if (p != IntPtr.Zero) { ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(p, 2))(p); p = IntPtr.Zero; } }

    static readonly Guid IidTex2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    static void Loop()
    {
        IntPtr dev = IntPtr.Zero, ctx = IntPtr.Zero, shared = IntPtr.Zero, staging = IntPtr.Zero;
        uint curHandle = 0, curW = 0, curH = 0, curFmt = 0;
        while (true)
        {
            try
            {
                if (!ControlState.Current.FaceCam.On) { Status = "off"; Thread.Sleep(1000); continue; }
                if (dev == IntPtr.Zero)
                {
                    int hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out dev, out _, out ctx);
                    if (hr < 0) { Status = $"no D3D11 device (0x{hr:X8})"; dev = IntPtr.Zero; Thread.Sleep(5000); continue; }
                }
                uint handle, w, h, fmt;
                try
                {
                    using var mm = MemoryMappedFile.OpenExisting(SenderName, MemoryMappedFileRights.Read);
                    using var v = mm.CreateViewAccessor(0, 16, MemoryMappedFileAccess.Read);
                    handle = v.ReadUInt32(0); w = v.ReadUInt32(4); h = v.ReadUInt32(8); fmt = v.ReadUInt32(12);
                }
                catch (FileNotFoundException) { Status = "waiting for OBS's face cam feed"; Release(ref staging); Release(ref shared); curHandle = 0; Thread.Sleep(500); continue; }
                if (handle == 0 || w == 0 || h == 0 || w > 4096 || h > 4096) { Thread.Sleep(500); continue; }
                if (handle != curHandle || w != curW || h != curH || fmt != curFmt)
                {
                    Release(ref staging); Release(ref shared);
                    var iid = IidTex2D;
                    IntPtr tex;
                    int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)V(dev, 28))(dev, (IntPtr)(long)handle, &iid, &tex);
                    if (hr < 0) { Status = $"can't open the shared picture (0x{hr:X8})"; curHandle = 0; Thread.Sleep(1000); continue; }
                    shared = tex;
                    TexDesc d;
                    ((delegate* unmanaged[Stdcall]<IntPtr, TexDesc*, void>)V(shared, 10))(shared, &d);
                    var sd = new TexDesc { Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = 1, Format = d.Format, SampleCount = 1, SampleQuality = 0, Usage = 3, BindFlags = 0, CpuAccess = 0x20000, Misc = 0 };
                    IntPtr st;
                    hr = ((delegate* unmanaged[Stdcall]<IntPtr, TexDesc*, IntPtr, IntPtr*, int>)V(dev, 5))(dev, &sd, IntPtr.Zero, &st);
                    if (hr < 0) { Status = $"can't make the copy texture (0x{hr:X8})"; Release(ref shared); curHandle = 0; Thread.Sleep(1000); continue; }
                    staging = st;
                    curHandle = handle; curW = d.Width; curH = d.Height; curFmt = d.Format;
                    Program.Log($"face cam: receiving OBS's \"{SenderName}\" {curW}x{curH} (format {curFmt})");
                }
                // copy the shared picture into the staging texture, read it on the CPU
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)V(ctx, 47))(ctx, staging, shared);
                Mapped m;
                int mhr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, Mapped*, int>)V(ctx, 14))(ctx, staging, 0, 1, 0, &m);
                if (mhr >= 0)
                {
                    int ww = (int)curW, hh = (int)curH;
                    var buf = new byte[ww * hh * 4];
                    fixed (byte* dst = buf)
                    {
                        for (int y = 0; y < hh; y++)
                            Buffer.MemoryCopy((byte*)m.Data + (long)y * m.RowPitch, dst + (long)y * ww * 4, ww * 4, ww * 4);
                    }
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)V(ctx, 15))(ctx, staging, 0);
                    // a frozen picture (OBS stopped rendering the source) reads the same bytes over and over: only count changes
                    bool changed;
                    lock (gate) { changed = latest == null || !Same(latest, buf); }
                    if (changed)
                    {
                        lock (gate) { latest = buf; latestW = ww; latestH = hh; latestBgra = curFmt != 28 && curFmt != 29; latestSeq++; }
                        lastFrameMs = clock.Elapsed.TotalMilliseconds;
                        Status = $"receiving {ww}x{hh}";
                    }
                }
            }
            catch (Exception e) { Status = "error: " + e.Message; Program.Log("face cam: " + e.Message); Thread.Sleep(2000); }
            Thread.Sleep(33);
        }
    }

    // cheap change test: a few hundred sampled pixels
    static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        int step = Math.Max(4, (a.Length / 512) & ~3);
        for (int i = 0; i < a.Length; i += step) if (a[i] != b[i] || a[i + 1] != b[i + 1]) return false;
        return true;
    }
}
