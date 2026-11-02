using System.Runtime.InteropServices;
using System.Text;
using ApexHUD.Native.Data;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// #492 (2026-10-30, Angelo: the loading splash "doesn't show the replay or track being loaded - just shows the old
/// screen"): a read-only look at iRacing's own shared telemetry block while the sim is still loading, before the
/// telemetry feed (IRSDKSharper) reports it connected. The sim writes its session text (WeekendInfo: track, layout,
/// country, SimMode replay/full) into that block; if it is already there during the load, the splash can name the
/// track straight away.
/// <para><b>Never stale.</b> The block outlives the sim while anything still holds it open, so it can still hold the
/// LAST run's session. Its contents are only used when the block was seen GONE while the sim was not running (so this
/// block was made by the sim that is loading now); otherwise nothing is used, and the log says so.</para>
/// </summary>
internal static class IRacingMapPeek
{
    const string MapName = "Local\\IRSDKMemMapFileName";
    const uint FileMapRead = 0x0004;
    const int MaxInfo = 4 * 1024 * 1024;

    /// <summary>What the block holds right now. <see cref="Exists"/> false = no block (the sim isn't up, and nothing
    /// holds an old one). Status bit 1 = the sim says it is connected.</summary>
    public readonly record struct Result(bool Exists, int Status, int InfoUpdate, string? Info);

    public static Result Read()
    {
        IntPtr map = IntPtr.Zero, view = IntPtr.Zero;
        try
        {
            map = OpenFileMapping(FileMapRead, false, MapName);
            if (map == IntPtr.Zero) return new Result(false, 0, 0, null);
            view = MapViewOfFile(map, FileMapRead, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero) return new Result(true, 0, 0, null);
            // irsdk_header: ver, status, tickRate, sessionInfoUpdate, sessionInfoLen, sessionInfoOffset, ...
            int status = Marshal.ReadInt32(view, 4);
            int update = Marshal.ReadInt32(view, 12);
            int len = Marshal.ReadInt32(view, 16);
            int off = Marshal.ReadInt32(view, 20);
            string? info = null;
            if (len > 0 && len <= MaxInfo && off > 0)
            {
                var buf = new byte[len];
                Marshal.Copy(view + off, buf, 0, len);
                int end = Array.IndexOf(buf, (byte)0);
                info = Encoding.Latin1.GetString(buf, 0, end < 0 ? len : end);
            }
            return new Result(true, status, update, info);
        }
        catch { return new Result(map != IntPtr.Zero, 0, 0, null); }
        finally
        {
            if (view != IntPtr.Zero) UnmapViewOfFile(view);
            if (map != IntPtr.Zero) CloseHandle(map);
        }
    }

    /// <summary>The splash fields from a session text, or null when it names no track. Only WeekendInfo's own lines
    /// (one space in): TrackDisplayName, TrackName, TrackConfigName, TrackCountry, TrackAirTemp, SimMode.</summary>
    public static SplashInfo? Parse(string? info)
    {
        if (string.IsNullOrEmpty(info)) return null;
        var f = new Dictionary<string, string>(StringComparer.Ordinal);
        bool inWeekend = false;
        foreach (var raw in info.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line[0] != ' ') { inWeekend = line.StartsWith("WeekendInfo:", StringComparison.Ordinal); continue; }
            if (!inWeekend || line.Length < 2 || line[1] == ' ') continue;          // only the block's own keys
            int colon = line.IndexOf(':');
            if (colon < 2) continue;
            f[line[1..colon]] = line[(colon + 1)..].Trim();
        }
        string Get(string k) => f.TryGetValue(k, out var v) ? v : "";
        string name = Get("TrackDisplayName");
        if (name.Length == 0) return null;
        return new SplashInfo(name, Get("TrackName"), Get("TrackConfigName"), Get("TrackCountry"), "", Array.Empty<string>(), "",
                              RaceStateService.ParseTempC(Get("TrackAirTemp"))) { SimMode = Get("SimMode") };
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offHigh, uint offLow, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
}
