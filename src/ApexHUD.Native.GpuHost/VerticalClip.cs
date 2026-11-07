using System.Diagnostics;
using System.Globalization;
using System.IO;
using ApexHUD.Native.Common;
using ApexHUD.Native.Data;
using ApexHUD.Native.Overlays;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// 2026-11-06 (Angelo: "do the vertical clip stuff"): one button turns the last N seconds of the stream into a 9:16 clip
/// for YouTube Shorts / TikTok / Instagram Reels.
///   1. OBS's Replay Buffer keeps the last ~45 s of the program output in memory (Grandstand switches it on in OBS's
///      settings and starts it while OBS is open - Integrations > Vertical clips "keep the buffer running").
///   2. Clip it (Producer page, Integrations page, Stream Deck "--do clip", chat "!clip" if allowed): OBS saves the buffer,
///      ffmpeg cuts the last N seconds and lays it out 1080x1920 - the race picture full width across the middle over a
///      blurred copy of itself, Grandstand's words above (track, lap, the moment) and below (driver, channel name).
///   3. The clip lands in Videos\Grandstand\Clips; with "Upload clips to YouTube" on (and YouTube signed in) it goes up as
///      a Short (#Shorts in the title).
/// ffmpeg: found on PATH / winget / C:\ffmpeg; the Integrations page has a "Get ffmpeg" button (winget Gyan.FFmpeg).
/// Encodes on the graphics card (NVENC) at below-normal priority, falls back to x264.
/// </summary>
internal static class VerticalClip
{
    static RaceStateService? rs;
    static Action<string> log = _ => { };
    static int busy;
    public static bool Busy => Volatile.Read(ref busy) == 1;
    public static string Status { get; private set; } = "";
    public static string LastClip { get; private set; } = "";
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Grandstand", "Clips");
    static long lastChatTicks;

    public static void Init(RaceStateService raceState, Action<string> logger) { rs = raceState; log = logger; }

    static Integrations.Settings S => Integrations.Current;

    /// <summary>Host loop (every 5 s): keep OBS's Replay Buffer running while clips are on and OBS is open.</summary>
    static long nextEnsure;
    public static void Tick()
    {
        bool want = S.ClipOn && S.ClipKeepBuffer;
        ObsMarkers.WantStatus = want;
        if (!want) return;
        ObsMarkers.Start();
        var st = ObsMarkers.Status;
        if (!st.Linked || st.Buffer || Environment.TickCount64 < nextEnsure) return;
        nextEnsure = Environment.TickCount64 + 60_000;   // at most once a minute
        _ = Task.Run(async () =>
        {
            var r = await ObsMarkers.EnsureReplayBufferAsync(BufferSeconds);
            if (!r.Ok) Status = r.Problem;
        });
    }
    static int BufferSeconds => Math.Clamp(S.ClipSeconds + 15, 30, 120);

    // ------------------------------------------------------------------ ffmpeg
    static string? ffmpeg; static bool? nvenc;
    public static string? FindFfmpeg()
    {
        if (ffmpeg != null && File.Exists(ffmpeg)) return ffmpeg;
        var tries = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            tries.Add(Path.Combine(dir.Trim(), "ffmpeg.exe"));
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        tries.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe"));
        try { foreach (var u in Directory.GetDirectories(@"C:\Users")) tries.Add(Path.Combine(u, "AppData", "Local", "Microsoft", "WinGet", "Links", "ffmpeg.exe")); } catch { }
        tries.Add(@"C:\ffmpeg\bin\ffmpeg.exe");
        tries.Add(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"));
        ffmpeg = tries.FirstOrDefault(File.Exists);
        if (ffmpeg == null)
        {
            // winget package folders (the Links shim may be missing when Grandstand runs as admin)
            try
            {
                foreach (var u in Directory.GetDirectories(@"C:\Users"))
                {
                    string pk = Path.Combine(u, "AppData", "Local", "Microsoft", "WinGet", "Packages");
                    if (!Directory.Exists(pk)) continue;
                    ffmpeg = Directory.EnumerateFiles(pk, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (ffmpeg != null) break;
                }
            }
            catch { }
        }
        return ffmpeg;
    }

    /// <summary>Installs ffmpeg with winget (the Integrations page's "Get ffmpeg" button).</summary>
    public static string GetFfmpeg()
    {
        if (FindFfmpeg() != null) return "ffmpeg is already here.";
        try
        {
            Process.Start(new ProcessStartInfo("winget", "install -e --id Gyan.FFmpeg --accept-source-agreements --accept-package-agreements") { UseShellExecute = true });
            Status = "Installing ffmpeg - a window opens; when it's done, press Clip it again.";
            ffmpeg = null;
            return Status;
        }
        catch (Exception ex) { return "Couldn't start the installer: " + ex.Message + ". Install ffmpeg from ffmpeg.org (or winget install Gyan.FFmpeg)."; }
    }

    static async Task<(int Code, string Err)> Run(string exe, string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }   // never steal from the sim
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEndAsync();
        var done = await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(timeoutMs));
        if (!p.HasExited) { try { p.Kill(true); } catch { } return (-1, "took too long"); }
        string e = await err; string o = await outp;
        return (p.ExitCode, (e + "\n" + o).Trim());
    }

    // ------------------------------------------------------------------ making a clip
    /// <summary>Make a clip of the last N seconds (returns at once; Status says how it went).</summary>
    public static string Request(string by)
    {
        if (!S.ClipOn) return "Vertical clips are switched off (Integrations > Vertical clips).";
        if (by.StartsWith("chat", StringComparison.Ordinal))
        {
            if (!S.ClipChat) return "";
            if (Environment.TickCount64 - Interlocked.Read(ref lastChatTicks) < 60_000) return "A clip was just made - try again in a minute.";
            Interlocked.Exchange(ref lastChatTicks, Environment.TickCount64);
        }
        if (Interlocked.Exchange(ref busy, 1) == 1) return "Already making a clip - one moment.";
        var info = Describe();
        Status = "Saving the last " + S.ClipSeconds + " seconds from OBS…";
        _ = Task.Run(async () =>
        {
            try { await Make(info, by); }
            catch (Exception ex) { Status = "Clip failed: " + ex.Message; log("clip: " + ex); }
            finally { Interlocked.Exchange(ref busy, 0); }
        });
        return $"Clipping the last {S.ClipSeconds} seconds…";
    }

    sealed record Info(string Eyebrow, string Title, string Sub, string FileTitle);

    /// <summary>The words for the clip, taken NOW (the moment the button was pressed).</summary>
    static Info Describe()
    {
        var s = rs?.Current;
        if (s == null || !s.IsConnected) s = ProducerDesk.OnScreen?.Invoke();   // iRacing closed: the sample race on screen
        if (s == null || s.ClassLeaderboard.Count == 0) return new Info("", "Race highlight", "", "Clip");
        string track = (s.TrackBroadcastName is { Length: > 0 } tb ? tb : s.TrackRawName ?? "").Trim();
        var me = s.Player ?? s.CameraTarget;
        int lap = me?.CurrentLap ?? s.CurrentLap;
        string eyebrow = string.Join("  ·  ", new[] { track, lap > 0 ? "Lap " + lap : "" }.Where(x => x.Length > 0));
        // the moment: the newest highlight in the last 45 s, else where we are
        var mark = HighlightLog.Current.LastOrDefault(m => (DateTime.UtcNow - m.Utc).TotalSeconds < 45);
        string title = mark?.Label is { Length: > 0 } ml ? ml
            : me != null && me.PositionInClass > 0 ? $"P{me.PositionInClass} in {ClassLogo.DisplayName(me.ClassName)}" : "Race highlight";
        string sub = me != null ? $"#{me.CarNumber} {NameFormat.Broadcast(me)}  ·  {me.CarName}" : "";
        string file = string.Join(" - ", new[] { title, track }.Where(x => x.Length > 0));
        return new Info(eyebrow, title, sub, file);
    }

    static async Task Make(Info info, string by)
    {
        string? ff = FindFfmpeg();
        if (ff == null) { Status = "ffmpeg isn't installed - press \"Get ffmpeg\" on the Integrations page (one time)."; return; }
        // 1. OBS: the buffer must already be running (it holds the past)
        var rb = await ObsMarkers.EnsureReplayBufferAsync(BufferSeconds);
        if (!rb.Ok) { Status = "OBS: " + rb.Problem; return; }
        if (!rb.WasRecording) { Status = $"OBS's Replay Buffer was off - it's on now. It needs {S.ClipSeconds} seconds of racing before the first clip; press again then."; return; }
        var saved = await ObsMarkers.SaveReplayAsync();
        if (!saved.Ok || !File.Exists(saved.OutputPath)) { Status = "OBS: " + (saved.Problem.Length > 0 ? saved.Problem : "the replay file wasn't found"); return; }
        // OBS may still be finishing the file
        long lastLen = -1;
        for (int i = 0; i < 20; i++) { long len = new FileInfo(saved.OutputPath).Length; if (len > 0 && len == lastLen) break; lastLen = len; await Task.Delay(300); }
        Status = "Making the vertical clip…";

        // 2. how tall the picture is at 1080 wide
        int vh = 608;
        string probe = Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe.exe");
        if (File.Exists(probe))
        {
            var (pc, po) = await Run(probe, $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 \"{saved.OutputPath}\"", 15000);
            var parts = po.Split('\n')[0].Split(',');
            if (pc == 0 && parts.Length >= 2 && int.TryParse(parts[0], out int sw) && int.TryParse(parts[1], out int sh) && sw > 0)
                vh = Math.Clamp((int)Math.Round(1080.0 * sh / sw / 2) * 2, 300, 1920);
        }
        // 3. the words + frame
        Directory.CreateDirectory(Folder);
        string overlay = Path.Combine(Path.GetTempPath(), "grandstand_clip_overlay.png");
        File.WriteAllBytes(overlay, ClipOverlay.Render(info.Eyebrow, info.Title, info.Sub, S.ClipHandle.Trim(), vh));
        string safe = string.Concat(info.FileTitle.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch));
        if (safe.Length > 60) safe = safe[..60];
        string outPath = Path.Combine(Folder, $"Clip {DateTime.Now:yyyy-MM-dd HH-mm-ss} - {safe}.mp4");
        int secs = Math.Clamp(S.ClipSeconds, 10, 60);
        string filter = "[0:v]scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,boxblur=28:2,eq=brightness=-0.22:saturation=1.1[bg];"
                      + "[0:v]scale=1080:-2[fg];[bg][fg]overlay=0:(H-h)/2[v1];[v1][1:v]overlay=0:0,format=yuv420p[v]";
        string common = $"-y -hide_banner -loglevel error -sseof -{secs} -i \"{saved.OutputPath}\" -i \"{overlay}\" -filter_complex \"{filter}\" -map \"[v]\" -map 0:a? -c:a aac -b:a 160k -movflags +faststart -t {secs}";
        nvenc ??= (await Run(ff, "-hide_banner -encoders", 15000)).Err.Contains("h264_nvenc", StringComparison.Ordinal);
        var sw2 = Stopwatch.StartNew();
        (int Code, string Err) res = (-1, "");
        if (nvenc == true) res = await Run(ff, $"{common} -c:v h264_nvenc -preset p5 -rc vbr -cq 20 -b:v 12M -maxrate 20M \"{outPath}\"", 180_000);
        if (res.Code != 0) res = await Run(ff, $"{common} -c:v libx264 -preset veryfast -crf 20 \"{outPath}\"", 300_000);
        try { File.Delete(overlay); } catch { }
        if (res.Code != 0 || !File.Exists(outPath)) { Status = "ffmpeg couldn't make the clip: " + Short(res.Err); log("clip: ffmpeg " + res.Err); return; }
        LastClip = outPath;
        Status = $"Clip ready: {Path.GetFileName(outPath)} ({sw2.Elapsed.TotalSeconds:0} s to make)";
        log($"clip: {outPath} ({by}, {sw2.Elapsed.TotalSeconds:0.0} s, {(nvenc == true ? "nvenc" : "x264")})");
        if (!S.ClipKeepSource) try { File.Delete(saved.OutputPath); } catch { }

        // 4. YouTube Shorts (optional)
        if (S.ClipUpload && YouTubeUpload.HasClient && YouTubeUpload.SignedIn)
        {
            Status = "Clip ready - uploading it to YouTube as a Short…";
            string title = $"{info.Title} | {info.Eyebrow.Replace("  ·  ", " · ")} #Shorts";
            string desc = $"{info.Title}\n{info.Eyebrow.Replace("  ·  ", " · ")}\n{info.Sub.Replace("  ·  ", " · ")}\n\n#iRacing #simracing #Shorts\nBroadcast by Grandstand";
            try
            {
                var (url, privacy) = await YouTubeUpload.Upload(outPath, title, desc, S.YouTubePrivacy);
                Status = $"Clip uploaded ({privacy}): {url}";
                if (privacy != "private" && S.DiscordOnHighlights) await Integrations.PostHighlights(info.Title + " (Short)", url);
            }
            catch (Exception ex) { Status = "Clip saved, but the YouTube upload failed: " + ex.Message; }
        }
    }

    /// <summary>2026-11-06: what a clip will look like, from the Live view's current picture (Integrations page preview),
    /// 540x960 PNG: the same layout ffmpeg makes - blurred, darkened copy filling the frame, the picture across the middle,
    /// Grandstand's words on top.</summary>
    public static byte[]? Preview(byte[] frameJpeg)
    {
        using var src = SkiaSharp.SKBitmap.Decode(frameJpeg);
        if (src == null) return null;
        const int W = 1080, H = 1920;
        using var surf = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(W, H, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul));
        var c = surf.Canvas;
        c.Clear(SkiaSharp.SKColors.Black);
        float cover = (float)H / src.Height, bw = src.Width * cover;
        using (var bg = new SkiaSharp.SKPaint
        {
            ImageFilter = SkiaSharp.SKImageFilter.CreateBlur(26, 26),
            ColorFilter = SkiaSharp.SKColorFilter.CreateColorMatrix(new float[] { 0.75f, 0, 0, 0, 0, 0, 0.75f, 0, 0, 0, 0, 0, 0.75f, 0, 0, 0, 0, 0, 1, 0 }),
        })
            c.DrawBitmap(src, new SkiaSharp.SKRect((W - bw) / 2, 0, (W + bw) / 2, H), bg);
        int vh = (int)Math.Round(1080.0 * src.Height / src.Width / 2) * 2;
        using (var fg = new SkiaSharp.SKPaint { FilterQuality = SkiaSharp.SKFilterQuality.High })
            c.DrawBitmap(src, new SkiaSharp.SKRect(0, (H - vh) / 2f, W, (H + vh) / 2f), fg);
        var info = Describe();
        using (var ov = SkiaSharp.SKBitmap.Decode(ClipOverlay.Render(info.Eyebrow, info.Title, info.Sub, S.ClipHandle.Trim(), vh)))
            if (ov != null) c.DrawBitmap(ov, 0, 0);
        using var img = surf.Snapshot();
        using var small = new SkiaSharp.SKBitmap(540, 960);
        using (var full = SkiaSharp.SKBitmap.FromImage(img)) full.ScalePixels(small, SkiaSharp.SKFilterQuality.High);
        using var data = small.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    static string Short(string s) { s = s.Replace("\r", " ").Replace("\n", " ").Trim(); return s.Length > 220 ? s[^220..] : s; }

    public sealed record ClipFile(string Name, long Size, DateTime At);
    public static IReadOnlyList<ClipFile> Recent(int n = 12)
    {
        try
        {
            if (!Directory.Exists(Folder)) return Array.Empty<ClipFile>();
            return new DirectoryInfo(Folder).GetFiles("*.mp4").OrderByDescending(f => f.LastWriteTime).Take(n)
                .Select(f => new ClipFile(f.Name, f.Length, f.LastWriteTime)).ToList();
        }
        catch { return Array.Empty<ClipFile>(); }
    }

    public static string? PathOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains("..") || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        string p = Path.Combine(Folder, name);
        return File.Exists(p) ? p : null;
    }

    public static object StatusObject() => new
    {
        on = S.ClipOn, busy = Busy, status = Status, last = Path.GetFileName(LastClip), folder = Folder,
        ffmpeg = FindFfmpeg() != null, obs = ObsMarkers.Status.Linked, buffer = ObsMarkers.Status.Buffer,
        recent = Recent().Select(c => new { name = c.Name, mb = Math.Round(c.Size / 1048576.0, 1), at = c.At.ToString("ddd HH:mm", CultureInfo.InvariantCulture) }),
    };
}
