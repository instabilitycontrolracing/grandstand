using System.IO;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Phase 2 timing-tower transition frame sequences. Pre-extracted JPEG frames
/// (bundled under <c>assets/transitions/frames/&lt;clip&gt;/fNNN.jpg</c> at lane
/// width, 30 fps) play as the splash background for scenes that have a clip.
/// Scenes without a clip (LMP2, anything unmapped) fall back to the Phase 1
/// animated background — the renderer decides per scene.
///
/// <para><b>Memory.</b> Frames are decoded on demand with a tiny single-frame
/// cache, so only one ~480 px JPEG is ever resident. Decoding one small JPEG
/// per 30 fps draw is cheap; we deliberately do NOT pre-decode whole clips into
/// RAM — that would defeat the project's low-RAM goal.</para>
///
/// <para><b>Clip keys.</b> Derived from the scene's label, lower-cased
/// (<c>OVERALL</c>→<c>overall</c>, <c>GTP</c>→<c>gtp</c>, <c>GT3</c>→<c>gt3</c>).
/// A clip "exists" only when its folder was bundled with frames; the frame
/// count is read once at startup by enumerating the folder.</para>
///
/// <para><b>Sources (2026-10-24, public build).</b> The bundled frames are the owner's real IMSA
/// footage: PERSONAL, never in git, never shipped. The transition source is now a choice
/// (<see cref="Mode"/>, set from the control app's Settings):
/// <c>auto</c> (default) = per class: the personal frames folder if present, else the user's own
/// rollers in <c>Documents\iRacing\Grandstand\transitions\&lt;gt3|gtp|lmp2|gtd|overall&gt;\</c>
/// (jpg/png sequences, sorted by name, 30 fps; README.txt there), else the built-in animated stinger
/// (<see cref="ProceduralStinger"/>); <c>builtin</c> = always the animated stinger; <c>folder</c> = the
/// user's rollers where present, else the stinger; <c>off</c> = no transition, the tower just swaps
/// boards. Frame lists are swapped whole (immutable arrays), so the render thread never sees a
/// half-built table; a clip that is already playing keeps the list it started with (FrameStreamer).</para>
/// </summary>
public static class SceneTransitions
{
    /// <summary>Frame rate the clips were extracted at — drives both the per-scene
    /// splash duration (frameCount / Fps) and the frame index at draw time.</summary>
    public const double Fps = 30.0;

    public const string ModeAuto = "auto", ModeBuiltIn = "builtin", ModeFolder = "folder", ModeOff = "off";
    public static readonly string[] Modes = { ModeAuto, ModeBuiltIn, ModeFolder, ModeOff };
    /// <summary>Folder names a user can fill with their own roller frames.</summary>
    public static readonly string[] UserKeys = { "gt3", "gtp", "lmp2", "gtd", "overall" };
    public static readonly string UserFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "transitions");
    const int MaxUserFrames = 900;   // 30 s at 30 fps

    // clip key -> frame paths, per source; "active" is what the current mode plays.
    static volatile Dictionary<string, string[]> personal = new(StringComparer.OrdinalIgnoreCase);
    static volatile Dictionary<string, string[]> user = new(StringComparer.OrdinalIgnoreCase);
    static volatile Dictionary<string, string[]> active = new(StringComparer.OrdinalIgnoreCase);
    static volatile string mode = ModeAuto;
    static readonly object scanLock = new();

    // Single-frame decode cache (current frame only — bounded memory).
    static string? cachedKey;
    static int cachedIndex = -1;
    static SKBitmap? cachedBitmap;
    static readonly object cacheLock = new();

    static SceneTransitions()
    {
        Rescan();
    }

    /// <summary>Current choice: auto / builtin / folder / off.</summary>
    public static string Mode => mode;

    /// <summary>True when transitions are switched off (the tower swaps boards without a splash).</summary>
    public static bool Off => mode == ModeOff;

    /// <summary>Switch the source (control app). Unknown values mean auto. Cheap when unchanged.</summary>
    public static void SetMode(string? m)
    {
        m = Modes.Contains(m ?? "") ? m! : ModeAuto;
        if (m == mode) return;
        lock (scanLock) { mode = m; Rebuild(); }
    }

    /// <summary>Re-read the personal frames folder and the user's rollers folder (startup, and when the
    /// control app asks after the user dropped new frames in).</summary>
    public static void Rescan()
    {
        lock (scanLock)
        {
            var p = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var root = Assets.Resolve("transitions", "frames");
                if (Directory.Exists(root))
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        // personal footage: fNNN.jpg from f001, exactly as before
                        int count = Directory.EnumerateFiles(dir, "*.jpg").Count();
                        if (count <= 0) continue;
                        var key = Path.GetFileName(dir);
                        var list = new string[count];
                        for (int i = 0; i < count; i++) list[i] = Assets.Resolve("transitions", "frames", key, $"f{i + 1:000}.jpg");
                        p[key] = list;
                    }
            }
            catch { }
            var u = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Directory.CreateDirectory(UserFolder);
                WriteReadme();
                foreach (var key in UserKeys)
                {
                    var dir = Path.Combine(UserFolder, key);
                    Directory.CreateDirectory(dir);
                    var files = Directory.EnumerateFiles(dir)
                        .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                                 || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                        .Take(MaxUserFrames).ToArray();
                    if (files.Length > 0) u[key] = files;
                }
            }
            catch { }
            personal = p; user = u;
            Rebuild();
        }
    }

    static void Rebuild()
    {
        var a = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (mode == ModeAuto)
        {
            foreach (var kv in user) a[kv.Key] = kv.Value;
            foreach (var kv in personal) a[kv.Key] = kv.Value;    // personal wins over the user's folder
        }
        else if (mode == ModeFolder)
            foreach (var kv in user) a[kv.Key] = kv.Value;
        active = a;
    }

    /// <summary>The clip key for a scene if a frame sequence plays for it in the current mode,
    /// else null (the renderer then draws the built-in animated stinger).</summary>
    public static string? ClipKeyFor(Scene? scene)
    {
        if (scene == null) return null;
        var key = scene.Label.ToLowerInvariant();
        return active.ContainsKey(key) ? key : null;
    }

    /// <summary>The frame paths of a clip in the current mode (null if none). FrameStreamer keeps the
    /// array it started with, so a mode change mid-clip can't mix two sequences.</summary>
    public static string[]? Frames(string clipKey) =>
        active.TryGetValue(clipKey, out var f) ? f : null;

    /// <summary>Number of frames bundled for a clip (0 if unknown).</summary>
    public static int FrameCount(string clipKey) =>
        active.TryGetValue(clipKey, out var f) ? f.Length : 0;

    /// <summary>Full splash duration for a clip, in ms: frameCount / Fps.
    /// "Play the clip in full" — the splash lasts exactly the footage length.</summary>
    public static double ClipDurationMs(string clipKey) =>
        FrameCount(clipKey) / Fps * 1000.0;

    /// <summary>"Preserve mine" (owner, 2026-10-24): in Auto on a PC with personal footage, a class without
    /// frames (LMP2) keeps the original 1.2 s Phase 1 splash exactly as before, not the built-in stinger.
    /// Public installs (no personal frames anywhere) get the stinger.</summary>
    public static bool UseLegacySplash(Scene? scene) =>
        mode == ModeAuto && personal.Count > 0 && ClipKeyFor(scene) == null;

    /// <summary>How long the splash for <paramref name="scene"/> lasts: the clip length when frames play,
    /// the old Phase 1 length on the owner's layout (<see cref="UseLegacySplash"/>), else the built-in
    /// stinger's length (the same as the footage it replaces).</summary>
    public static double SplashDurationMs(Scene scene)
    {
        var key = ClipKeyFor(scene);
        if (key != null) return ClipDurationMs(key);
        return UseLegacySplash(scene) ? SceneDirector.DefaultSplashTotalMs : ProceduralStinger.DurationMs(scene);
    }

    /// <summary>"personal", "user" or "built-in" for each class folder name, for the control app.</summary>
    public static object Describe()
    {
        var a = active; var p = personal; var u = user;
        var keys = UserKeys.Concat(p.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new
        {
            mode,
            folder = UserFolder,
            personalInstalled = p.Count > 0,
            classes = keys.Select(k => new
            {
                key = k,
                source = mode == ModeOff ? "off" : a.TryGetValue(k, out var f) ? (p.TryGetValue(k, out var pf) && ReferenceEquals(pf, f) ? "personal" : "user") : "built-in",
                frames = a.TryGetValue(k, out var f2) ? f2.Length : 0,
                userFrames = u.TryGetValue(k, out var uf) ? uf.Length : 0,
            }).ToArray(),
        };
    }

    /// <summary>Decode the JPEG for a frame index (0-based) of a clip, or null if
    /// it can't be loaded. Cached single-frame: re-requesting the same frame is
    /// free; advancing disposes the previous frame so memory stays bounded.</summary>
    public static SKBitmap? LoadFrame(string clipKey, int index)
    {
        lock (cacheLock)
        {
            if (cachedKey == clipKey && cachedIndex == index && cachedBitmap != null)
                return cachedBitmap;

            var frames = Frames(clipKey);
            var path = frames != null && index >= 0 && index < frames.Length ? frames[index] : null;
            SKBitmap? bmp = null;
            try
            {
                if (path != null && File.Exists(path))
                {
                    using var stream = File.OpenRead(path);
                    bmp = SKBitmap.Decode(stream);
                }
            }
            catch
            {
                bmp = null;
            }

            cachedBitmap?.Dispose();
            cachedBitmap = bmp;
            cachedKey = clipKey;
            cachedIndex = index;
            return bmp;
        }
    }

    /// <summary>Background startup pre-warm: pull every clip frame into the OS
    /// file cache and prime the JPEG codec on the first few of each, so the FIRST
    /// tower transition is not reading ~180 cold files off disk + initialising the
    /// codec mid-playback (the clunky-on-first-load stutter). Safe off-thread;
    /// every decode is discarded, nothing is retained.</summary>
    public static void WarmUp()
    {
        try
        {
            foreach (var kv in active)
            {
                var frames = kv.Value;
                for (int i = 0; i < frames.Length; i++)
                {
                    var path = frames[i];
                    if (!File.Exists(path)) continue;
                    try
                    {
                        if (i < 8)
                        {
                            using var stream = File.OpenRead(path);
                            using var bmp = SKBitmap.Decode(stream); // warms codec + cache
                        }
                        else
                        {
                            _ = File.ReadAllBytes(path); // warms OS file cache cheaply
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    static void WriteReadme()
    {
        var readme = Path.Combine(UserFolder, "README.txt");
        if (File.Exists(readme)) return;
        File.WriteAllText(readme,
            "Grandstand - your own class transitions (\"rollers\")\r\n" +
            "==================================================\r\n\r\n" +
            "When the timing tower changes board (OVERALL -> GTP -> LMP2 -> GT3) a short transition covers it.\r\n" +
            "Grandstand draws its own animated one. To play your own instead, put a picture sequence in the\r\n" +
            "folder for that board:\r\n\r\n" +
            "  overall\\   gtp\\   lmp2\\   gt3\\   gtd\\\r\n\r\n" +
            "- jpg or png frames, played in file-name order (f001.jpg, f002.jpg, ...) at 30 frames a second;\r\n" +
            "  the transition lasts as long as your sequence (up to 900 frames = 30 s; 3-6 s works best).\r\n" +
            "- Best size: 480 x 1046 (tall, the shape of the tower). Bigger pictures are shrunk and cropped to\r\n" +
            "  the middle when they load. Keep the middle of the sequence fully covered: the board swaps\r\n" +
            "  underneath halfway through.\r\n" +
            "- Make a sequence from a video with ffmpeg, e.g.:\r\n" +
            "    ffmpeg -i myroller.mp4 -vf \"fps=30,scale=-2:1046,crop=480:1046\" -q:v 3 gt3\\f%03d.jpg\r\n" +
            "- Then in the control page: Settings -> Class transitions -> \"My rollers folder\" (or Auto) and\r\n" +
            "  press Rescan.\r\n\r\n" +
            "Only use footage you made or have the rights to show on a stream.\r\n");
    }
}
