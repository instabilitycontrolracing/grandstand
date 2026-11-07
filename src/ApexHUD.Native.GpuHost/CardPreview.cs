using System.Threading;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// 2026-11-05 (Angelo: "somewhere should actually allow you to see what the cards look like while you edit them"):
/// the card editor asks for a picture of one graphic; the RENDER thread draws it with the real renderer (same code,
/// same settings - position, size, see-through, picture, theme) onto an offscreen canvas over a racing backdrop and
/// hands back a PNG. Live data when iRacing is running, Grandstand's sample race when it isn't.
/// One request at a time; the control page asks a few times a second at most while the editor is open.
/// </summary>
internal static class CardPreview
{
    static readonly SemaphoreSlim one = new(1, 1);
    static readonly ManualResetEventSlim done = new(false);
    static volatile string? pending;
    static byte[]? result;
    static bool resultEmpty, resultLive;

    /// <summary>Control thread: ask for a preview and wait for the render thread (null on timeout / unknown card).</summary>
    public static (byte[]? png, bool empty, bool live) Request(string id, int timeoutMs = 2000)
    {
        if (!one.Wait(timeoutMs)) return (null, false, false);
        try
        {
            result = null; done.Reset();
            pending = id;
            if (!done.Wait(timeoutMs)) { pending = null; return (null, false, false); }
            return (result, resultEmpty, resultLive);
        }
        finally { one.Release(); }
    }

    /// <summary>Render thread: the card wanted now, if any.</summary>
    public static string? Take() { var p = pending; if (p != null) pending = null; return p; }

    /// <summary>Render thread: the finished picture.</summary>
    public static void Deliver(byte[]? png, bool empty, bool live) { result = png; resultEmpty = empty; resultLive = live; done.Set(); }
}
