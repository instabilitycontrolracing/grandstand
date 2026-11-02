namespace ApexHUD.Native.Overlays;

/// <summary>2026-10-25: the overlays' own log lines (contacts seen, diary restored, cards played...) go into Grandstand's
/// log file. Console.WriteLine went nowhere in the windowed app, so these were invisible while debugging.</summary>
public static class OverlayLog
{
    public static Action<string>? Sink;
    public static void Write(string msg) { try { Sink?.Invoke(msg); } catch { } }
}
