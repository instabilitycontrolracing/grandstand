using System.IO;
using System.Runtime.InteropServices;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// Spout2 output for OBS (2026-10-26, #30 streaming plan step 1). When switched on in the control app
/// (Settings → Stream output), every finished frame - see-through parts included - is handed to Spout on the
/// graphics card, so OBS (with the free Spout2 plugin, source "Spout2 Capture", sender "Grandstand") shows the
/// overlay with perfect transparency and no screen capture. OFF by default; costs nothing while off.
/// <para>Uses Spout's own SpoutLibrary.dll (BSD-2, bundled in native\, licence next to it). Its methods are
/// called straight through the C++ object's virtual table. Only the first seven slots are used: MSVC moves
/// overloaded methods (GetName, GetAdapterInfo, ...) next to each other, so slot numbers further down the
/// header can't be trusted - the first seven come before any overload and match SpoutLibrary.h (2.007.018).</para>
/// </summary>
internal static unsafe class SpoutOut
{
    const string SenderName = "Grandstand";
    // virtual-table slots in SPOUTLIBRARY (2.007.018)
    const int SlotSetSenderName = 0, SlotReleaseSender = 2, SlotSendFbo = 3, SlotIsInitialized = 6;

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr mod, string name);

    static IntPtr lib, spout;
    static bool failed, sending, announced;
    static int lastW, lastH;

    static IntPtr Slot(int i) => (*(IntPtr**)spout)[i];

    static bool Load()
    {
        if (spout != IntPtr.Zero) return true;
        if (failed) return false;
        try
        {
            string p = Path.Combine(AppContext.BaseDirectory, "native", "SpoutLibrary.dll");
            if (!File.Exists(p)) { Program.Log("Spout: SpoutLibrary.dll not found next to Grandstand - stream output off"); failed = true; return false; }
            lib = LoadLibraryW(p);
            if (lib == IntPtr.Zero) { Program.Log($"Spout: could not load SpoutLibrary.dll (error {Marshal.GetLastWin32Error()})"); failed = true; return false; }
            var get = GetProcAddress(lib, "GetSpout");
            if (get == IntPtr.Zero) { Program.Log("Spout: GetSpout missing"); failed = true; return false; }
            spout = ((delegate* unmanaged[Stdcall]<IntPtr>)get)();
            if (spout == IntPtr.Zero) { Program.Log("Spout: GetSpout returned nothing"); failed = true; return false; }
            var name = Marshal.StringToHGlobalAnsi(SenderName);
            try { ((delegate* unmanaged<IntPtr, IntPtr, void>)Slot(SlotSetSenderName))(spout, name); }
            finally { Marshal.FreeHGlobal(name); }
            return true;
        }
        catch (Exception e) { Program.Log($"Spout: {e.Message}"); failed = true; spout = IntPtr.Zero; return false; }
    }

    /// <summary>Called on the render thread after the frame is drawn and flushed, before SwapBuffers
    /// (the GL context is current). Sends the window's framebuffer.</summary>
    public static void Frame(bool on, int w, int h)
    {
        if (!on)
        {
            if (sending) Stop();
            return;
        }
        if (w <= 0 || h <= 0 || !Load()) return;
        try
        {
            // default framebuffer (0); GL is bottom-up, so Spout flips it the right way up for receivers
            byte ok = ((delegate* unmanaged<IntPtr, uint, uint, uint, byte, byte>)Slot(SlotSendFbo))(spout, 0, (uint)w, (uint)h, 1);
            if (!sending || w != lastW || h != lastH)
            {
                sending = true; lastW = w; lastH = h;
                bool init = ((delegate* unmanaged<IntPtr, byte>)Slot(SlotIsInitialized))(spout) != 0;
                Program.Log($"Spout: sending \"{SenderName}\" {w}x{h} (first frame {(ok != 0 ? "ok" : "not taken")}, sender {(init ? "started" : "not started")})");
                announced = true;
            }
        }
        catch (Exception e) { Program.Log($"Spout: send failed ({e.Message}) - stream output off until restart"); failed = true; spout = IntPtr.Zero; sending = false; }
    }

    public static void Stop()
    {
        if (spout == IntPtr.Zero || !sending) { sending = false; return; }
        try { ((delegate* unmanaged<IntPtr, uint, void>)Slot(SlotReleaseSender))(spout, 0); } catch { }
        sending = false;
        if (announced) Program.Log("Spout: stream output stopped");
    }
}
