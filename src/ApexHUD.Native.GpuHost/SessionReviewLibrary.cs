using System.IO;
using ApexHUD.Native.Common;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// 2026-10-27 — finds the most recent session for the TV's SESSION REVIEW: the newest finished, non-stub .ibt in iRacing's
/// telemetry folder (Documents\iRacing\telemetry) or the Race Engineer's archive (E:\RaceEngineer\telemetry), analysed
/// once (in the background, low priority) and kept as JSON in Documents\iRacing\Grandstand\session_review so a restart
/// doesn't read the 250 MB file again. Checked every minute while no session is live, every 10 s for the first few
/// minutes after one ends (iRacing closes the file when you leave the car / session), never while live.
/// A file iRacing still has open, or written in the last 20 s, is skipped until it's finished.
/// </summary>
internal sealed class SessionReviewLibrary
{
    static readonly string[] Folders =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "telemetry"),
        @"E:\RaceEngineer\telemetry",
    };
    static readonly string CacheDir = Path.Combine(ControlState.Folder, "session_review");

    SessionReviewData? current;
    volatile bool scanning;
    long nextScanMs, liveEndedMs = long.MinValue / 2;
    bool wasLive;
    string lastKey = "";
    readonly HashSet<string> stubs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The review to show (null until one is found).</summary>
    public SessionReviewData? Current => Volatile.Read(ref current);

    /// <summary>Host loop (every 250 ms).</summary>
    public void Tick(long nowMs, bool live)
    {
        if (live) { wasLive = true; return; }
        if (wasLive) { wasLive = false; liveEndedMs = nowMs; nextScanMs = nowMs + 5000; }
        if (scanning || nowMs < nextScanMs) return;
        bool soon = nowMs - liveEndedMs < 5 * 60_000;
        nextScanMs = nowMs + (soon || current == null ? 10_000 : 60_000);
        scanning = true;
        var t = new Thread(() =>
        {
            try { Scan(); }
            catch (Exception ex) { Program.Log("review: scan failed: " + ex.Message); }
            finally { scanning = false; }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "session review scan" };
        t.Start();
    }

    void Scan()
    {
        var files = new List<FileInfo>();
        foreach (var dir in Folders)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var di = new DirectoryInfo(dir);
                files.AddRange(di.EnumerateFiles("*.ibt", SearchOption.TopDirectoryOnly));
                foreach (var sub in di.EnumerateDirectories()) files.AddRange(sub.EnumerateFiles("*.ibt", SearchOption.TopDirectoryOnly));
            }
            catch { /* a missing drive or folder is fine */ }
        }
        if (files.Count == 0) return;
        // newest first; the same file in both places counts once
        var ordered = files.OrderByDescending(f => f.LastWriteTimeUtc).GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Take(12).ToList();
        string key = ordered[0].FullName + "|" + ordered[0].Length + "|" + ordered[0].LastWriteTimeUtc.Ticks;
        if (key == lastKey && current != null) return;
        bool settled = true;
        foreach (var f in ordered)
        {
            string fk = f.FullName + "|" + f.Length + "|" + f.LastWriteTimeUtc.Ticks;
            if (stubs.Contains(fk)) continue;
            if (current != null && string.Equals(current.SourceFile, f.FullName, StringComparison.OrdinalIgnoreCase)
                && current.SourceSize == f.Length && current.SourceWriteUtc == f.LastWriteTimeUtc) { lastKey = key; return; }   // already showing it
            if ((DateTime.UtcNow - f.LastWriteTimeUtc).TotalSeconds < 20 || !Finished(f.FullName)) { settled = false; continue; }   // still being written
            var d = LoadCached(f) ?? Analyse(f, fk);
            if (d == null) continue;
            Volatile.Write(ref current, d);
            Program.Log($"review: now showing {f.Name} ({d.SessionType}, {d.LapsCompleted} laps, best {d.BestLap:0.000}, {d.Segments.Count} stops)");
            break;
        }
        if (settled) lastKey = key;
    }

    /// <summary>True when nobody is writing the file any more (iRacing keeps it open for writing during the session).</summary>
    static bool Finished(string path)
    {
        try { using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return true; }
        catch { return false; }
    }

    static string CacheFile(FileInfo f) => Path.Combine(CacheDir, Path.GetFileNameWithoutExtension(f.Name) + ".json");

    static SessionReviewData? LoadCached(FileInfo f)
    {
        string c = CacheFile(f);
        if (!File.Exists(c)) return null;
        var d = SessionReviewAnalyzer.Load(c);
        return d != null && d.SourceSize == f.Length && d.SourceWriteUtc == f.LastWriteTimeUtc ? d with { SourceFile = f.FullName } : null;
    }

    SessionReviewData? Analyse(FileInfo f, string fk)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SessionReviewData? d;
        try { d = SessionReviewAnalyzer.Analyze(f.FullName); }
        catch (Exception ex) { Program.Log($"review: could not read {f.Name}: {ex.Message}"); d = null; }
        if (d == null) { stubs.Add(fk); Program.Log($"review: {f.Name} has no completed lap - skipped"); return null; }
        Program.Log($"review: analysed {f.Name} in {sw.ElapsedMilliseconds} ms");
        try
        {
            SessionReviewAnalyzer.Save(d, CacheFile(f));
            // keep the folder small: the 40 newest reviews
            var old = new DirectoryInfo(CacheDir).GetFiles("*.json").OrderByDescending(x => x.LastWriteTimeUtc).Skip(40);
            foreach (var o in old) o.Delete();
        }
        catch (Exception ex) { Program.Log("review: could not save: " + ex.Message); }
        return d;
    }
}
