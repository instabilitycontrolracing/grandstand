namespace ApexHUD.Native.Overlays;

/// <summary>
/// Light mode (2026-10-24, Angelo: "is it something the average person could use reliably"): for slower PCs.
/// When on, the overlay paces itself at 30 fps instead of 60, redraws plain data changes 10 times a second
/// instead of 20, and skips the purely decorative motion (light sweeps, flag wipes, shines, live waveforms).
/// Everything still shows; it just moves less. "auto" turns it on by itself if frames get heavy.
/// </summary>
public static class Perf
{
    /// <summary>"auto" (default), "full" or "light" - from the control app.</summary>
    public static string Mode { get; set; } = "auto";
    static volatile bool autoLight;

    /// <summary>True when the decorative motion should be skipped.</summary>
    public static bool Light => Mode == "light" || (Mode == "auto" && autoLight);

    /// <summary>Present interval: 60 fps normally, 30 in light mode.</summary>
    public static double PresentIntervalMs => Light ? 33.3 : 16.6;

    /// <summary>Plain data-change redraw throttle: 20 Hz normally, 10 Hz in light mode.</summary>
    public static double DataRedrawMs => Light ? 100 : 50;

    static int heavyWindows, windowsSeen;
    /// <summary>Fed every 10 s with the average frame cost (draw + flush, ms). Auto: three heavy windows in a row (30 s,
    /// more than 12 ms a frame - the overlay alone eating most of a 60 fps budget) switch light mode on for
    /// the rest of the run; it never flaps back and forth mid-race.</summary>
    public static bool ReportFrameCost(double avgMs)
    {
        if (Mode != "auto" || autoLight) return false;
        if (++windowsSeen <= 6) return false;      // the first minute is warm-up (images loading onto the GPU), not the PC's real speed
        heavyWindows = avgMs > 12 ? heavyWindows + 1 : 0;
        if (heavyWindows >= 3) { autoLight = true; return true; }
        return false;
    }
}
