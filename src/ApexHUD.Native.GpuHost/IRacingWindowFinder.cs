using System.Text;
using static ApexHUD.Native.GpuHost.NativeInterop;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// Finds and tracks iRacing's real render window. Replicates the targeting the
/// Session-26 POC validated: enumerate visible top-level windows whose title
/// contains "iRacing", skip the hidden launcher stub parked at (-32000,-32000)
/// 160×25, and pick the largest reasonably-sized on-screen window (the real
/// "iRacing.com Simulator" surface). The overlay is then sized/positioned to
/// match it so the tower lane lands exactly where it does in the WPF overlay.
/// </summary>
internal static class IRacingWindowFinder
{
    public readonly record struct Target(IntPtr Handle, int X, int Y, int Width, int Height, string Title);

    /// <summary>Pick the best iRacing window, or null if none is on screen.</summary>
    public static Target? Find()
    {
        var cands = new List<Target>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            int len = GetWindowTextLength(h);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            var title = sb.ToString();
            if (title.IndexOf("iRacing", StringComparison.OrdinalIgnoreCase) < 0) return true;
            if (title == "iRacing") return true;   // the iRacing menus app, never the sim (2026-10-24)
            // 2026-10-27 (#342): never a console window - his launcher's "Administrator:  START iRacing" cmd window on the
            // main monitor was being followed while the sim loaded, so the overlays sat on his monitor
            var cls = new StringBuilder(64);
            if (GetClassName(h, cls, cls.Capacity) > 0 && cls.ToString() is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS") return true;
            if (ClientBounds(h) is not { } r) return true;
            cands.Add(new Target(h, r.X, r.Y, r.W, r.H, title));
            return true;
        }, IntPtr.Zero);

        // the sim's own window first ("iRacing.com Simulator"); any other "iRacing" title (a browser tab,
        // the iRacing UI, Discord...) only if the sim window isn't there
        var sim = cands.Where(c => c.Title.StartsWith("iRacing.com Simulator", StringComparison.Ordinal)).ToList();
        if (sim.Count > 0) cands = sim;
        Target? best = null;
        long bestArea = -1;
        foreach (var c in cands)
        {
            // On screen (not the -32000 parking spot) and reasonably large.
            if (c.X <= -10000 || c.Y <= -10000 || c.Width < 640 || c.Height < 360) continue;
            long area = (long)c.Width * c.Height;
            if (area > bestArea) { bestArea = area; best = c; }
        }
        return best;
    }

    /// <summary>2026-10-27 (loading splash): the iRacing menus app is open - a visible top-level window titled
    /// exactly "iRacing" (the sim's own window is "iRacing.com Simulator").</summary>
    public static bool MenusWindowOpen()
    {
        bool found = false;
        var sb = new StringBuilder(16);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindowTextLength(h) != 7) return true;
            sb.Clear();
            GetWindowText(h, sb, 16);
            if (sb.ToString() == "iRacing") { found = true; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Current bounds of a known target handle, or null if it's gone /
    /// no longer visible (so the caller can re-Find).</summary>
    public static Target? Track(IntPtr handle, string title)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle) || !IsWindowVisible(handle)) return null;
        if (ClientBounds(handle) is not { } r) return null;
        if (r.X <= -10000 || r.Y <= -10000 || r.W < 640 || r.H < 360) return null;
        return new Target(handle, r.X, r.Y, r.W, r.H, title);
    }

    /// <summary>The window's drawing area on screen (no title bar / borders), so in windowed mode the
    /// overlay lines up with the picture, not the frame. Borderless: same as the window rect.</summary>
    static (int X, int Y, int W, int H)? ClientBounds(IntPtr h)
    {
        if (!GetClientRect(h, out RECT c)) return null;
        var p = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(h, ref p)) return null;
        return (p.X, p.Y, c.Right - c.Left, c.Bottom - c.Top);
    }

    struct POINT { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
}
