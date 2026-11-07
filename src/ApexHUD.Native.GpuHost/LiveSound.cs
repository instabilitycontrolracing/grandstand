using System.IO;
using ApexHUD.Native.Overlays;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// 2026-11-06 (Angelo: "why the sound doesn't work" in the Live view): Grandstand's own sound is routed (Windows, per app)
/// to the TV cable, never the headset - so the sample race's music and stingers were playing on the TV. While the sample race
/// runs, every cue and opener track is listed here and the Live view page plays it inside the app window (WebView2 uses the
/// normal speakers / headset). GET /api/live/sounds?since=N and GET /api/live/sound/{seq}.
/// </summary>
internal static class LiveSound
{
    public sealed record Item(int Seq, string Kind, string Path, double MaxSeconds, long AtMs);
    static readonly object gate = new();
    static readonly List<Item> items = new();
    static int seq;

    public static void Init()
    {
        var prev = StorySound.OnPlay;
        StorySound.OnPlay = cue =>
        {
            try { prev?.Invoke(cue); } catch { }
            if (!OverlayCompositor.SampleRunning || cue.Contains("(skipped", StringComparison.Ordinal)) return;
            var p = SoundFiles.Resolve(cue + ".wav");
            if (p != null) Add("cue", p, 0);
        };
        OpenerAudio.OnOpen = (file, max) => { if (OverlayCompositor.SampleRunning) Add("music", file, max); };
    }

    static void Add(string kind, string path, double max)
    {
        lock (gate)
        {
            items.Add(new Item(++seq, kind, path, max, Environment.TickCount64));
            if (items.Count > 40) items.RemoveAt(0);
        }
    }

    /// <summary>Sounds after <paramref name="since"/> that are still fresh (a page that was away doesn't get old ones).</summary>
    public static object Since(int since)
    {
        long now = Environment.TickCount64;
        lock (gate)
            return new { last = seq, items = items.Where(i => i.Seq > since && now - i.AtMs < 4000).Select(i => new { seq = i.Seq, kind = i.Kind, max = i.MaxSeconds, url = "/api/live/sound/" + i.Seq }).ToList() };
    }

    public static (byte[] Data, string Type)? File(int s)
    {
        Item? it;
        lock (gate) it = items.FirstOrDefault(i => i.Seq == s);
        if (it == null || !System.IO.File.Exists(it.Path)) return null;
        string ext = Path.GetExtension(it.Path).ToLowerInvariant();
        string type = ext switch { ".mp3" => "audio/mpeg", ".ogg" => "audio/ogg", ".m4a" => "audio/mp4", ".flac" => "audio/flac", _ => "audio/wav" };
        try { return (System.IO.File.ReadAllBytes(it.Path), type); } catch { return null; }
    }
}
