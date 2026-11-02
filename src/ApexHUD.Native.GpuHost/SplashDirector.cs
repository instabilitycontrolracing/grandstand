using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using ApexHUD.Native.Common;
using ApexHUD.Native.Data;
using ApexHUD.Native.Overlays;
using static ApexHUD.Native.GpuHost.NativeInterop;

namespace ApexHUD.Native.GpuHost;

/// <summary>Where iRacing is, as far as the loading splash cares (2026-10-27).</summary>
internal enum SplashState
{
    /// <summary>No iRacing menus app and no sim: no loading splash; the session review plays (2026-10-27) when it's on.</summary>
    Off,
    /// <summary>The iRacing menus app is up, the sim is not: generic "STAND BY" splash (no track - nothing is known).</summary>
    Menus,
    /// <summary>The sim window / process is up but no telemetry yet: splash filled from SessionInfo as it arrives.</summary>
    Loading,
    /// <summary>Telemetry flowing and the sim window present (the main overlay's own "show" condition).</summary>
    Live,
}

/// <summary>
/// 2026-10-27 — owns the TV loading splash: works out <see cref="SplashState"/> in the host's 250 ms tracking loop,
/// debounces it (hand-off only after ~1 s of continuous Live; the splash comes back only after ~3 s out of Live, so
/// a telemetry blip or a session change in the same sim run never flashes it), builds the splash content from real
/// SessionInfo fields only, and keeps the splash window on the TV / Apollo 4K screen. The splash window (its own
/// <see cref="NativeOverlayHost"/>: click-through, never activated, topmost) is created the first time it is needed.
/// </summary>
internal sealed class SplashDirector : IDisposable
{
    const long HandOffAfterLiveMs = 1000, ReturnAfterLiveMs = 3000, MaxHoldMs = 6000;

    readonly RaceStateService rs;
    readonly LoadingSplash splash = new();
    // 2026-10-27: SESSION REVIEW - the last session's breakdown on a loop while no session is live (same TV window)
    readonly SessionReviewShow review = new();
    readonly SessionReviewLibrary library = new();
    volatile bool splashVisible, reviewVisible;
    SessionReviewShow.Command reviewCmd = SessionReviewShow.Command.Hide, lastReviewLogged = (SessionReviewShow.Command)(-1);
    NativeOverlayHost? host;

    long lastProcMs = long.MinValue / 2, lastTvMs = long.MinValue / 2;
    bool procSim, procUi;
    TvDisplay.Screen? tv;
    string tvLogged = "?";
    long liveSince = -1, lastLiveMs = long.MinValue / 2;
    SplashState state = SplashState.Off;
    long liveNoWinSince = -1;
    LoadingSplash.Command cmd = LoadingSplash.Command.Hide;

    public SplashDirector(RaceStateService rs)
    {
        this.rs = rs;
        SessionReviewShow.Log = Program.Log;
    }

    /// <summary>The state worked out on the last tick (for the log / control app).</summary>
    public SplashState State => state;

    /// <summary>True while the splash still covers the TV at the start of a live session: the main overlay waits
    /// for the dissolve instead of popping up on top of the plate (never longer than 6 s, whatever happens).</summary>
    public bool HoldMainOverlay(long nowMs) => splash.Holding && liveSince >= 0 && nowMs - liveSince < MaxHoldMs;

    /// <summary>Host loop, every 250 ms. <paramref name="tracked"/> is the window the main overlay follows.</summary>
    public void Tick(long nowMs, IRacingWindowFinder.Target? tracked, bool demo)
    {
        try { TickCore(nowMs, tracked, demo); }
        catch (Exception ex) { Program.Log("splash: " + ex.Message); }
    }

    void TickCore(long nowMs, IRacingWindowFinder.Target? tracked, bool demo)
    {
        // ---- where is iRacing? ----
        bool simWindow = tracked is { } t && t.Title.StartsWith("iRacing.com Simulator", StringComparison.Ordinal);
        if (nowMs - lastProcMs >= 1000)          // backup check, at most once a second
        {
            lastProcMs = nowMs;
            procSim = Running("iRacingSim64DX11");
            procUi = Running("iRacingUI");
        }
        bool sim = simWindow || procSim;
        bool ui = !sim && (procUi || IRacingWindowFinder.MenusWindowOpen());
        // 2026-10-27 (#342): belt and braces - the sim process running with a live session counts as Live after 5 s
        // even if the window we follow isn't the sim's (the splash must never sit on the TV over a running session)
        if (procSim && rs.IsSessionLive) { if (liveNoWinSince < 0) liveNoWinSince = nowMs; } else liveNoWinSince = -1;
        bool liveAnyway = liveNoWinSince >= 0 && nowMs - liveNoWinSince >= 5000;
        var next = (simWindow || liveAnyway) && rs.IsSessionLive ? SplashState.Live
                 : sim ? SplashState.Loading
                 : ui ? SplashState.Menus
                 : SplashState.Off;
        if (demo) next = SplashState.Off;
        if (next != state) Program.Log($"splash: iRacing {state} -> {next}");
        state = next;
        EarlyLook(nowMs);

        // ---- the TV screen (re-checked every 2 s while iRacing is up: Apollo's screen can come and go) ----
        bool enabled = ControlState.Current.LoadingSplash;
        bool reviewOn = ControlState.Current.SessionReview && !demo;
        if ((state != SplashState.Off && (enabled || reviewOn) || (state == SplashState.Off && reviewOn)) && nowMs - lastTvMs >= (state == SplashState.Off ? 5000 : 2000))
        {
            lastTvMs = nowMs;
            tv = TvDisplay.Find(out string all);
            string desc = tv is { } s ? $"{s.Device} {s.Width}x{s.Height} @ ({s.X},{s.Y})" : "none";
            if (desc != tvLogged)
            {
                tvLogged = desc;
                Program.Log($"splash: TV screen = {desc}   (screens: {all})");
            }
            if (tv is { } scr) host?.SetBounds(scr.X, scr.Y, scr.Width, scr.Height);
        }

        // ---- the session review: which session, and the TV's size ----
        library.Tick(nowMs, state == SplashState.Live);
        if (tv is { } tvs) review.SetData(reviewOn ? library.Current : null, ControlState.Current.TempUnit != "C", tvs.Width, tvs.Height);
        bool reviewReady = reviewOn && tv != null && review.HasContent;

        // ---- debounce into a command for the splash ----
        if (state == SplashState.Live)
        {
            if (liveSince < 0) liveSince = nowMs;
            lastLiveMs = nowMs;
            if (nowMs - liveSince >= HandOffAfterLiveMs) cmd = LoadingSplash.Command.HandOff;
        }
        else
        {
            liveSince = -1;
            // between sessions the review has the TV; the splash keeps the menus only when there's no review to play
            if (state == SplashState.Off || !enabled || tv == null || (state == SplashState.Menus && reviewReady)) cmd = LoadingSplash.Command.Hide;
            else if (nowMs - lastLiveMs >= ReturnAfterLiveMs) cmd = LoadingSplash.Command.Show;
            // else: just out of Live - keep whatever it was (stays handed off through a short gap)
        }

        // ---- and for the review: Off / Menus play it; Loading keeps it until the splash covers it; Live hides it ----
        bool between = state is SplashState.Off or SplashState.Menus;
        if (!reviewReady || state == SplashState.Live) reviewCmd = SessionReviewShow.Command.Hide;
        else if (between)
        {
            if (nowMs - lastLiveMs >= ReturnAfterLiveMs)
                reviewCmd = splash.Visible ? SessionReviewShow.Command.ShowNow : SessionReviewShow.Command.Show;   // under the splash: no see-through fade
        }
        else if (state == SplashState.Loading)
        {
            if (!enabled || splash.Covering) reviewCmd = SessionReviewShow.Command.Hide;   // the loading splash has taken over
        }

        // ---- content: only while menus / loading, from real data ----
        if (tv is { } screen && (state is SplashState.Menus or SplashState.Loading
                                  || (state == SplashState.Live && cmd != LoadingSplash.Command.HandOff)))   // fields that land in the last second still show
        {
            splash.SetContent(BuildContent(screen));
            if (cmd == LoadingSplash.Command.Show) EnsureHost(screen);
        }
        if (cmd != lastCmdLogged) { lastCmdLogged = cmd; Program.Log($"splash: command {cmd} (state {state}, tv={(tv != null)}, enabled={enabled})"); }
        splash.Send(cmd);
        if (reviewCmd != SessionReviewShow.Command.Hide && tv is { } rscr) EnsureHost(rscr);
        if (reviewCmd != lastReviewLogged) { lastReviewLogged = reviewCmd; Program.Log($"review: command {reviewCmd} (state {state}, tv={(tv != null)}, on={reviewOn}, ready={review.HasContent})"); }
        review.Send(reviewCmd);
    }
    LoadingSplash.Command lastCmdLogged = (LoadingSplash.Command)(-1);

    void EnsureHost(TvDisplay.Screen s)
    {
        if (host != null) return;
        // one window for both: the review underneath, the loading splash on top (it fades in over the review)
        host = new NativeOverlayHost(s.X, s.Y, s.Width, s.Height,
            render: (c, w, h, now) => { review.Render(c, w, h, now); splash.Render(c, w, h, now); },
            frameNeeded: now => { bool a = review.FrameNeeded(now); bool b = splash.FrameNeeded(now); return a || b; },
            onGpuReady: ctx => { splash.Gpu = ctx; review.Gpu = ctx; }, className: "ApexHudGpuSplashWindow", windowTitle: "Grandstand Splash",
            presentIntervalMs: () => splashVisible ? Math.Min(splash.PresentIntervalMs, review.PresentIntervalMs) : review.PresentIntervalMs);
        var h0 = host;
        splash.SetHostVisible = v => { splashVisible = v; h0.SetVisible(splashVisible || reviewVisible); };
        review.SetHostVisible = v => { reviewVisible = v; h0.SetVisible(splashVisible || reviewVisible); };
        host.Start();
        Program.Log($"splash: window created on the TV screen ({s.Width}x{s.Height} @ {s.X},{s.Y})");
    }

    // ---- #492 (2026-10-30): the track (and replay vs live) from iRacing's own shared data while the sim is still loading ----
    long earlyLookMs = long.MinValue / 2;
    bool mapGoneSeen;                 // the shared block was seen gone while the sim wasn't running: whatever it holds next is new
    SplashInfo? early;                // what the loading sim has already written (only when mapGoneSeen)
    bool earlyLogged;

    /// <summary>At most once a second: while the sim isn't running, notes whether iRacing's shared block is gone (then
    /// anything in it later belongs to the sim that is loading); while it loads and before the telemetry feed has the
    /// session, reads the track from it. One log line per load says what was there and whether it was used.</summary>
    void EarlyLook(long nowMs)
    {
        if (nowMs - earlyLookMs < 1000) return;
        earlyLookMs = nowMs;
        try
        {
            if (state is SplashState.Off or SplashState.Menus)
            {
                var r = IRacingMapPeek.Read();
                if (!r.Exists && !mapGoneSeen) Program.Log("splash: iRacing's shared data is closed (an early look at the next load can be trusted)");
                mapGoneSeen = !r.Exists;
                early = null; earlyLogged = false;
                return;
            }
            if (state != SplashState.Loading) { early = null; earlyLogged = false; return; }
            if (rs.Splash is { TrackName.Length: > 0 }) return;      // the feed has it now
            var res = IRacingMapPeek.Read();
            var parsed = IRacingMapPeek.Parse(res.Info);
            bool use = mapGoneSeen && parsed != null;
            if (use) early = parsed;
            if (!earlyLogged && (parsed != null || res.Exists))
            {
                earlyLogged = parsed != null;
                Program.Log($"splash: early look while loading - shared data {(res.Exists ? "open" : "not there yet")}, status {res.Status}, " +
                            $"info #{res.InfoUpdate} ({res.Info?.Length ?? 0} chars), track '{parsed?.TrackName ?? ""}', mode '{parsed?.SimMode ?? ""}' -> " +
                            (use ? "SHOWN" : parsed == null ? "nothing to show yet" : "NOT shown (the block was never seen closed, it may be the last run's)"));
            }
        }
        catch (Exception ex) { Program.Log("splash: early look: " + ex.Message); }
    }

    SplashContent BuildContent(TvDisplay.Screen s)
    {
        if (state == SplashState.Menus)
            return new SplashContent { Width = s.Width, Height = s.Height, Label = "STAND BY", Title = "NEXT SESSION", Status = "WAITING FOR THE SIM" };

        var info = rs.Splash;
        if ((info == null || info.TrackName.Length == 0) && state == SplashState.Loading && early != null) info = early;   // #492
        if (info == null || info.TrackName.Length == 0)
            return new SplashContent { Width = s.Width, Height = s.Height, Label = "COMING UP", Title = "NEXT SESSION", Status = "SIM LOADING",
                                       Cells = Cells(info) };

        string? map = null;
        try
        {
            var b = ApexHUD.Native.Overlays.TrackMapCache.Resolve(info.TrackConfig, info.TrackName, info.TrackRawName, null);
            if (b != null)
            {
                string f = Path.Combine(ApexHUD.Native.Overlays.Assets.Resolve("maps"), b + ".json");
                if (File.Exists(f)) map = f;
            }
        }
        catch { map = null; }

        var sub = new List<string>(2);
        if (info.TrackConfig.Length > 0) sub.Add(info.TrackConfig);
        if (info.TrackCountry.Length > 0) sub.Add(info.TrackCountry);
        return new SplashContent
        {
            // #492: a saved replay says so (WeekendInfo.SimMode), a live session is COMING UP
            Width = s.Width, Height = s.Height, Label = info.IsReplay ? "REPLAY" : "COMING UP", Title = info.TrackName, Subtitle = string.Join("  ·  ", sub),
            Status = LoadingWhat(info), Cells = Cells(info), MapFile = map,
        };
    }

    /// <summary>#476 (2026-10-28, Angelo: "say what is loading"): the status line names the session and the field
    /// iRacing is loading (the track is the big title) - only what SessionInfo says, e.g. "LOADING RACE · 54 CARS".</summary>
    static string LoadingWhat(SplashInfo info)
    {
        var parts = new List<string>(3);
        if (info.SessionType.Length > 0) parts.Add(info.SessionType.ToUpperInvariant());
        if (info.Cars > 0) parts.Add(info.Cars == 1 ? "1 CAR" : $"{info.Cars.ToString(CultureInfo.InvariantCulture)} CARS");
        if (info.IsReplay) return "LOADING REPLAY" + (parts.Count > 0 ? "  ·  " + string.Join("  ·  ", parts) : "");   // #492
        return parts.Count == 0 ? "SIM LOADING" : "LOADING " + string.Join("  ·  ", parts);
    }

    /// <summary>The bottom strip: only fields that are actually known.</summary>
    List<SplashCell> Cells(SplashInfo? info)
    {
        var cells = new List<SplashCell>(4);
        if (info != null)
        {
            if (info.SessionType.Length > 0) cells.Add(new SplashCell("SESSION", info.SessionType, true));
            if (info.Classes.Count > 0) cells.Add(new SplashCell(info.Classes.Count == 1 ? "CLASS" : "CLASSES", string.Join("  ·  ", info.Classes), false));
            string time = LocalTime(info.TimeOfDay);
            if (time.Length > 0) cells.Add(new SplashCell("LOCAL TIME", time, false));
        }
        var track = new List<string>(2);
        var snap = rs.Current;
        if (snap.IsConnected && snap.Extras is { } ex)
        {
            string wet = WetLabel(ex.TrackWetness);
            if (wet.Length > 0) track.Add(wet);
        }
        if (info != null && !float.IsNaN(info.AirTempC))
            track.Add(UnitPrefs.Fahrenheit(snap)
                ? $"{Math.Round(info.AirTempC * 9f / 5f + 32f).ToString(CultureInfo.InvariantCulture)}°F"
                : $"{Math.Round(info.AirTempC).ToString(CultureInfo.InvariantCulture)}°C");
        if (track.Count > 0) cells.Add(new SplashCell("TRACK", string.Join("  ·  ", track), false));
        return cells;
    }

    /// <summary>iRacing TrackWetness (live telemetry only): 0 unknown, 1 dry ... 7 extremely wet. Unknown -> hidden.</summary>
    static string WetLabel(int w) => w switch
    {
        1 => "DRY",
        2 => "MOSTLY DRY",
        3 or 4 => "LIGHTLY WET",
        5 => "WET",
        6 or 7 => "VERY WET",
        _ => "",
    };

    /// <summary>"2:30 pm" -> "14:30"; anything unparseable is shown as iRacing wrote it.</summary>
    static string LocalTime(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out var dt))
            return dt.ToString("HH:mm", CultureInfo.InvariantCulture);
        return raw.Trim().ToUpperInvariant();
    }

    static bool Running(string name)
    {
        try
        {
            var ps = Process.GetProcessesByName(name);
            foreach (var p in ps) p.Dispose();
            return ps.Length > 0;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        host?.Stop();
        host = null;
        splash.Dispose();
        review.Dispose();
    }
}

/// <summary>
/// 2026-10-27: finds the TV. Angelo's TV is fed by Apollo and shows up in Windows as an extra 4K screen; his main
/// monitor (D40f-J09) is the primary one. Rule: the largest NON-primary screen that is at least 3840x2160 (physical
/// pixels - the process is per-monitor DPI aware). No such screen -> no splash. The primary monitor is never used.
/// </summary>
internal static class TvDisplay
{
    public readonly record struct Screen(string Device, int X, int Y, int Width, int Height);

    public static Screen? Find(out string all)
    {
        var list = new List<(Screen S, bool Primary)>();
        MonitorEnumProc cb = (IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                var m = mi.rcMonitor;
                list.Add((new Screen(mi.szDevice ?? "?", m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top), (mi.dwFlags & MonitorInfoFPrimary) != 0));
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        all = list.Count == 0 ? "none" : string.Join(", ", list.Select(x => $"{x.S.Device} {x.S.Width}x{x.S.Height}{(x.Primary ? " primary" : "")}"));
        Screen? best = null;
        long bestArea = 0;
        foreach (var (s, primary) in list)
        {
            if (primary || s.Width < 3840 || s.Height < 2160) continue;
            long area = (long)s.Width * s.Height;
            if (area > bestArea) { bestArea = area; best = s; }
        }
        return best;
    }

    const uint MonitorInfoFPrimary = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
}
