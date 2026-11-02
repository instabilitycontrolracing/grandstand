using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// "Bring your own clock dial" (2026-10-24): which face the session-bar watch wears.
///
/// <para>Settings: <c>Documents\iRacing\Grandstand\clock.json</c> =
/// <c>{"dial": "&lt;choice&gt;", "subdialHands": true|false|null}</c>. User faces live in
/// <c>Documents\iRacing\Grandstand\dials\</c> (created on first run with a README.txt).</para>
///
/// <para>Choices: <c>classic</c> = assets\clock\dial.png (the owner's personal dial - only when the file
/// exists and is bigger than 64 px; it is never shipped), <c>homage_black|green|ivory|navy|panda</c> =
/// the bundled original dials, <c>file:&lt;name&gt;</c> = a picture in the dials folder, <c>auto</c>
/// (default) = classic if present, else homage_ivory. Built-ins and classic show the subdial accent
/// hands; custom pictures default them off unless <c>subdialHands</c> is true.</para>
///
/// <para>Decoding happens when a dial is selected (startup or the control app's thread), never per
/// frame. The new <see cref="Face"/> is swapped in whole and <see cref="Version"/> bumped; the watch
/// compares that int each frame. Anything bad (missing, unreadable, odd file) falls back to auto with
/// one log line - the watch never crashes or goes blank (a cream face is drawn under every image).</para>
/// </summary>
public static class ClockDial
{
    /// <summary>A ready-to-draw dial: the bitmap (null = plain cream face), where it goes in the
    /// watch's 100x100 viewBox, and whether the static subdial accent hands are drawn.</summary>
    public sealed record Face(SKBitmap? Bitmap, SKRect Dest, bool SubdialHands, string Choice, string Active);

    public static readonly string[] BuiltIns = { "homage_black", "homage_green", "homage_ivory", "homage_navy", "homage_panda" };
    public const string DefaultBuiltIn = "homage_ivory";
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp" };

    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand");
    public static readonly string FilePath = Path.Combine(Folder, "clock.json");
    public static readonly string DialsFolder = Path.Combine(Folder, "dials");

    /// <summary>Purpose-made dials (classic + homages) are laid out on the -4.56..104.56 viewBox: the
    /// legacy slot (6..94) cover-fit at 124%.</summary>
    public static readonly SKRect PurposeMadeDest = new(50 - 54.56f, 50 - 54.56f, 50 + 54.56f, 50 + 54.56f);
    /// <summary>Arbitrary pictures map their (centre-cropped) square onto the r=44 visible circle's box.</summary>
    public static readonly SKRect CustomDest = new(6, 6, 94, 94);

    const int MaxDecodeSide = 1024;
    const long MaxFileBytes = 40L * 1024 * 1024;

    /// <summary>Where log lines go (the GPU host wires this to its log).</summary>
    public static Action<string>? Log;

    static readonly object gate = new();
    static volatile Face current = new(null, CustomDest, false, "auto", "none");
    static volatile bool loaded;
    static int version;

    /// <summary>Bumped whenever the face changes; the watch compares it per frame (int compare only).</summary>
    public static int Version { get { Ensure(); return Volatile.Read(ref version); } }

    /// <summary>The face to draw now.</summary>
    public static Face Current { get { Ensure(); return current; } }

    /// <summary>Read clock.json and decode the chosen dial now (startup), not on the first frame.</summary>
    public static void Ensure()
    {
        if (loaded) return;
        lock (gate)
        {
            if (loaded) return;
            string choice = "auto"; bool? hands = null;
            try
            {
                Directory.CreateDirectory(DialsFolder);
                WriteReadme();
                if (File.Exists(FilePath))
                {
                    var o = JsonNode.Parse(File.ReadAllText(FilePath), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
                    choice = o?["dial"]?.GetValue<string>() ?? "auto";
                    hands = o?["subdialHands"] is JsonValue hv && hv.TryGetValue<bool>(out var b) ? b : null;
                }
                else Save("auto", null);
            }
            catch (Exception ex) { Log?.Invoke("clock dial: couldn't read clock.json (" + ex.Message + "), using auto"); choice = "auto"; hands = null; }
            SavedSubdialHands = hands;
            current = Build(choice, hands);
            Interlocked.Increment(ref version);
            loaded = true;
            Log?.Invoke($"clock dial: {current.Active}" + (current.Choice != current.Active ? $" (choice {current.Choice})" : "") + (current.SubdialHands ? "" : ", no subdial hands"));
        }
    }

    /// <summary>Switch dial live (control app). Unknown/bad choices fall back to auto. Saves clock.json.</summary>
    public static Face Select(string? choice, bool? subdialHands)
    {
        Ensure();
        choice = string.IsNullOrWhiteSpace(choice) ? "auto" : choice.Trim();
        lock (gate)
        {
            var f = Build(choice, subdialHands);
            current = f;
            Interlocked.Increment(ref version);
            try { Save(f.Choice, subdialHands); }
            catch (Exception ex) { Log?.Invoke("clock dial: couldn't save clock.json (" + ex.Message + ")"); }
            return f;
        }
    }

    /// <summary>The subdialHands value saved in clock.json (null = the dial's default).</summary>
    public static bool? SavedSubdialHands { get; private set; }

    /// <summary>True when the owner's personal dial (assets\clock\dial.png, &gt; 64 px) is installed.</summary>
    public static bool HasClassic()
    {
        try
        {
            string p = Assets.Resolve("clock", "dial.png");
            if (!File.Exists(p)) return false;
            using var codec = SKCodec.Create(p);
            return codec != null && codec.Info.Width > 64 && codec.Info.Height > 64;
        }
        catch { return false; }
    }

    /// <summary>Path of a bundled homage dial, or null if the name isn't one.</summary>
    public static string? BuiltInPath(string name) =>
        Array.IndexOf(BuiltIns, name) >= 0 ? Assets.Resolve("clock", name + ".png") : null;

    /// <summary>A plain file name of a picture in the dials folder (no folders, no "..", png/jpg/jpeg/webp),
    /// as its full path - or null.</summary>
    public static string? CustomPath(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120) return null;
        if (name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\') || name.Contains("..") || name.StartsWith('.')) return null;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        if (Array.IndexOf(ImageExtensions, Path.GetExtension(name).ToLowerInvariant()) < 0) return null;
        string full = Path.Combine(DialsFolder, name);
        return File.Exists(full) ? full : null;
    }

    /// <summary>Picture file names in the dials folder (sorted, capped at 200).</summary>
    public static List<string> ListCustom()
    {
        var list = new List<string>();
        try
        {
            if (!Directory.Exists(DialsFolder)) return list;
            foreach (var f in Directory.EnumerateFiles(DialsFolder))
            {
                string n = Path.GetFileName(f);
                if (CustomPath(n) != null) list.Add(n);
                if (list.Count >= 200) break;
            }
        }
        catch { }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    // ------------------------------------------------------------------ building a face

    static Face Build(string choice, bool? hands)
    {
        try
        {
            var f = TryBuild(choice, hands);
            if (f != null) return f;
            Log?.Invoke($"clock dial: '{choice}' isn't available, using auto");
        }
        catch (Exception ex) { Log?.Invoke($"clock dial: '{choice}' couldn't be loaded ({ex.Message}), using auto"); }
        try { return TryBuild("auto", null) ?? Blank(); }
        catch { return Blank(); }
    }

    static Face Blank() => new(null, CustomDest, true, "auto", "none");

    static Face? TryBuild(string choice, bool? hands)
    {
        if (choice == "auto")
        {
            var f = HasClassic() ? TryBuild("classic", hands) : null;
            f ??= TryBuild(DefaultBuiltIn, hands);
            return f is null ? null : f with { Choice = "auto" };
        }
        if (choice == "classic")
        {
            if (!HasClassic()) return null;
            return new(Assets.LoadBitmap("clock", "dial.png"), PurposeMadeDest, hands ?? true, "classic", "classic");
        }
        if (BuiltInPath(choice) is { } bp)
        {
            if (!File.Exists(bp)) return null;
            return new(Assets.LoadBitmap("clock", choice + ".png"), PurposeMadeDest, hands ?? true, choice, choice);
        }
        if (choice.StartsWith("file:", StringComparison.Ordinal))
        {
            string? p = CustomPath(choice[5..]);
            if (p == null) return null;
            return new(DecodeSquare(p), CustomDest, hands ?? false, choice, choice);
        }
        return null;
    }

    /// <summary>Decode a user picture at most ~1024 px, centre-cropped to a square, immutable.</summary>
    static SKBitmap DecodeSquare(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidOperationException("file is over 40 MB");
        using var codec = SKCodec.Create(path) ?? throw new InvalidOperationException("not a picture Grandstand can read");
        var info = codec.Info;
        if (info.Width < 8 || info.Height < 8) throw new InvalidOperationException("picture is too small");
        int shortSide = Math.Min(info.Width, info.Height);
        float want = Math.Min(1f, MaxDecodeSide / (float)shortSide);
        var dims = codec.GetScaledDimensions(want);   // JPEG/WebP decode straight to a smaller size
        var decInfo = new SKImageInfo(dims.Width, dims.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var full = new SKBitmap(decInfo);
        var r = codec.GetPixels(decInfo, full.GetPixels());
        if (r != SKCodecResult.Success && r != SKCodecResult.IncompleteInput)
            throw new InvalidOperationException("decode failed (" + r + ")");

        int s = Math.Min(dims.Width, dims.Height);
        var src = SKRect.Create((dims.Width - s) / 2, (dims.Height - s) / 2, s, s);
        int side = Math.Min(s, MaxDecodeSide);
        var outBmp = new SKBitmap(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var cv = new SKCanvas(outBmp))
        using (var p = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High })
        {
            cv.Clear(SKColors.Transparent);
            cv.DrawBitmap(full, src, new SKRect(0, 0, side, side), p);
        }
        outBmp.SetImmutable();
        return outBmp;
    }

    // ------------------------------------------------------------------ files

    static void Save(string choice, bool? hands)
    {
        SavedSubdialHands = hands;
        Directory.CreateDirectory(Folder);
        var o = new JsonObject
        {
            ["dial"] = choice,
            ["subdialHands"] = hands is bool b ? JsonValue.Create(b) : null,
        };
        File.WriteAllText(FilePath, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    static void WriteReadme()
    {
        string p = Path.Combine(DialsFolder, "README.txt");
        if (File.Exists(p)) return;
        File.WriteAllText(p,
@"Grandstand - your own broadcast clock dial
==========================================

Drop a picture here (PNG, JPG or WEBP) and pick it in the control app:
Event & sponsors -> Broadcast clock. It goes on air straight away.

- Any square-ish picture works. It is centre-cropped to a square and cut to a
  circle, so keep the important part in the middle.
- About 1000 x 1000 pixels is plenty; bigger pictures are scaled down.
- ""Subdial hands"": turn them on only if your dial has three small registers at
  the classic chronograph positions (9, 3 and 6 o'clock). Leave them off for a
  logo or artwork, or the little hands will float over it.
- With nothing chosen, Grandstand uses its own ""Ivory"" dial.

This slot is for sponsors, partners and creators' own artwork. Use artwork you
have the rights to.
");
    }
}
