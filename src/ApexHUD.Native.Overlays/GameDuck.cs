using System.Runtime.InteropServices;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// TV-stream ducking (Angelo 2026-10-23): while the broadcast intro music plays, the game / radio sound
/// going to the TV stream fades out, and fades back in as the music ends. Works through Voicemeeter's
/// Remote API on the bus that feeds the stream (B1 → VB-CABLE → Apollo); the headset bus (A1) is never
/// touched. Only active while the TV stream is on. Any missing piece (no Voicemeeter, no stream) = no-op.
/// The original gain is written to a small file first, so a crash mid-intro can't leave the TV silent:
/// the next start puts it back (<see cref="Recover"/>).
/// </summary>
public static class GameDuck
{
    const string Dll = @"C:\Program Files (x86)\VB\Voicemeeter\VoicemeeterRemote64.dll";
    // 2026-10-27 (#394, Angelo: "there should be no audio from iRacing so the opening music plays alone"): the bus that
    // feeds the TV stream is B1 (every strip goes to B1; A1 is the headset). "Bus[1]" was A2 - an empty bus - so the
    // duck never touched the stream. B1's index depends on the Voicemeeter edition: Voicemeeter 2, Banana 3, Potato 5.
    static string Param = "Bus[5].Gain";
    const float DuckDb = -60f;
    static readonly string StateFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Grandstand", "duck_gain.txt");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FLogin();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FDirty();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FGet([MarshalAs(UnmanagedType.LPStr)] string n, out float v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FSet([MarshalAs(UnmanagedType.LPStr)] string n, float v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FType(out int t);
    static FLogin? login; static FDirty? dirty; static FGet? get; static FSet? set;
    static bool tried, ok;
    static readonly object gate = new();
    static float restoreTo; static bool ducked;

    static bool Init()
    {
        if (tried) return ok;
        tried = true;
        try
        {
            if (!System.IO.File.Exists(Dll)) return false;
            var h = NativeLibrary.Load(Dll);
            login = Marshal.GetDelegateForFunctionPointer<FLogin>(NativeLibrary.GetExport(h, "VBVMR_Login"));
            dirty = Marshal.GetDelegateForFunctionPointer<FDirty>(NativeLibrary.GetExport(h, "VBVMR_IsParametersDirty"));
            get = Marshal.GetDelegateForFunctionPointer<FGet>(NativeLibrary.GetExport(h, "VBVMR_GetParameterFloat"));
            set = Marshal.GetDelegateForFunctionPointer<FSet>(NativeLibrary.GetExport(h, "VBVMR_SetParameterFloat"));
            int r = login();
            ok = r >= 0;
            if (ok) { Thread.Sleep(100); dirty(); }
            try
            {
                var type = Marshal.GetDelegateForFunctionPointer<FType>(NativeLibrary.GetExport(h, "VBVMR_GetVoicemeeterType"));
                if (type(out int vt) == 0) Param = vt switch { 1 => "Bus[2].Gain", 2 => "Bus[3].Gain", _ => "Bus[5].Gain" };
            }
            catch { }
        }
        catch { ok = false; }
        return ok;
    }

    /// <summary>The TV stream is on (Apollo running and the stream flag dropped by the "iRacing 4K" app).</summary>
    public static bool StreamOn()
    {
        try
        {
            return System.IO.File.Exists(@"C:\SimRacing\Tools\Stream4K\streaming.flag");
        }
        catch { return false; }
    }

    static float Read()
    {
        dirty!();
        return get!(Param, out var v) == 0 ? v : 0f;
    }

    /// <summary>Fade the stream bus down over <paramref name="ms"/>.</summary>
    public static void Begin(int ms = 350)
    {
        lock (gate)
        {
            if (ducked || !StreamOn() || !Init()) return;
            float g0 = Read();
            if (g0 < -40f) g0 = 0f;                 // already down from an earlier crash: aim for normal
            restoreTo = g0;
            try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StateFile)!); System.IO.File.WriteAllText(StateFile, g0.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch { }
            ducked = true;
            Ramp(g0, DuckDb, ms);
        }
    }

    /// <summary>Fade the stream bus back to where it was over <paramref name="ms"/>.</summary>
    public static void End(int ms = 300)
    {
        lock (gate)
        {
            if (!ducked) return;
            ducked = false;
            try { Ramp(Read(), restoreTo, ms); } catch { }
            try { set!(Param, restoreTo); System.IO.File.Delete(StateFile); } catch { }
        }
    }

    /// <summary>On startup: a gain left down by a crash mid-intro is put back.</summary>
    public static void Recover()
    {
        try
        {
            if (!System.IO.File.Exists(StateFile)) return;
            if (!Init()) return;
            float g = float.Parse(System.IO.File.ReadAllText(StateFile), System.Globalization.CultureInfo.InvariantCulture);
            set!(Param, g);
            System.IO.File.Delete(StateFile);
        }
        catch { }
    }

    static void Ramp(float from, float to, int ms)
    {
        int steps = Math.Max(1, ms / 40);
        for (int i = 1; i <= steps; i++)
        {
            float k = i / (float)steps;
            k = k * k * (3 - 2 * k);                 // smooth in and out
            set!(Param, from + (to - from) * k);
            Thread.Sleep(40);
        }
    }
}
