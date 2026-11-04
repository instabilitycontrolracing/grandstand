using System.Diagnostics;
using System.IO;
using System.Text;
using ApexHUD.Native.Data;

// ============================================================
//  ApexHUD GPU overlay host — Phase C
//  ONE native, GPU-composited, per-pixel-alpha transparent window that owns its
//  own render thread and drives MULTIPLE existing renderers (timing tower +
//  transition splash + featured card, this first batch) through a single shared
//  SKCanvas. The renderers are reused UNCHANGED — only the window/present layer
//  changed (see OverlayCompositor + DECISIONS.md). This does NOT replace the WPF
//  overlay; the WPF suite stays the instant rollback.
//
//  The headline behaviour is the ON-DEMAND render loop: the host presents ONLY
//  when something changed (a transition, a visibility flip, an animation, or a
//  data change) and idles otherwise — so static overlays cost ~0 GPU at rest and
//  the host bursts to 60 fps only while something is moving.
//
//  Run it alongside iRacing (a live race or replay with the sim window visible),
//  or with --demo to drive the canned snapshot. It self-logs both the present
//  CADENCE (burst quality) and the present RATE per 5 s window (idle vs burst) to
//  gpu_host_phaseC.txt. Ctrl+Shift+Q to quit.
//
//  Flags:  --demo        force the canned snapshot even when iRacing is connected
//          --seconds N   auto-quit after N seconds (bounded smoke test)
// ============================================================

namespace ApexHUD.Native.GpuHost
{
    internal static class Program
    {
        static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "gpu_host_phaseC.txt");

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [STAThread]
        static int Main(string[] args)
        {
            // a TV-stream duck left on by a crash mid-intro is put back (2026-10-23)
            System.Threading.Tasks.Task.Run(() => ApexHUD.Native.Overlays.GameDuck.Recover());
            // Offline render of the grid spotlight hero cards (no window/GL) for design review.
            if (args.Any(a => string.Equals(a, "--hero-shot", StringComparison.OrdinalIgnoreCase)))
                return HeroShot.Run();
            if (args.Any(a => string.Equals(a, "--bar-shot", StringComparison.OrdinalIgnoreCase)))
                return BarShot.Run(args);
            if (args.Any(a => string.Equals(a, "--flag-shot", StringComparison.OrdinalIgnoreCase)))
                return BarShot.Flags(args);

            // 2026-10-29 (efficiency): the render thread must never sit in a blocking full garbage collection mid-race -
            // background (concurrent) collections only, unless memory runs really low (then .NET still does what it must)
            try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency; } catch { }

            // Per-monitor DPI aware (2026-10-24): on a scaled display (125/150%, common at 4K) Windows would
            // otherwise stretch the overlay (blurry) and report shifted window positions. No change at 100%.
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            bool demo = args.Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase));
            bool demoGrid = args.Any(a => string.Equals(a, "--demo-grid", StringComparison.OrdinalIgnoreCase));
            if (demoGrid) demo = true;   // force the canned PreRace feed so the grid + hero cycle show

            int autoQuitSeconds = 0;
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "--seconds", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(args[i + 1], out int s)) autoQuitSeconds = s;

            // 2026-10-31: keep the last run's log (a race's frame-cost history was lost on every restart) - the previous
            // five runs stay in Documents\iRacing\gpu_host_logs\
            try
            {
                if (File.Exists(LogPath))
                {
                    string dir = Path.Combine(Path.GetDirectoryName(LogPath)!, "gpu_host_logs");
                    Directory.CreateDirectory(dir);
                    File.Copy(LogPath, Path.Combine(dir, $"gpu_host_{File.GetLastWriteTime(LogPath):yyyy-MM-dd_HH-mm-ss}.txt"), true);
                    foreach (var old in new DirectoryInfo(dir).GetFiles("gpu_host_*.txt").OrderByDescending(f => f.Name).Skip(40)) old.Delete();   // 40 runs (5 was used up by a night of restarts)
                }
            }
            catch { }
            try { File.WriteAllText(LogPath, $"# Grandstand GPU host (Phase C) — {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n"); }
            catch { /* logging is best-effort */ }

            // Live data feed (preferred). The compositor falls back to the canned
            // snapshot when disconnected, or always when --demo is set.
            using var raceState = new RaceStateService();
            long lastSdkErrLog = 0;
            raceState.Exception += ex =>
            {
                long t = Environment.TickCount64;
                if (t - lastSdkErrLog < 10000) return;      // at most one line per 10 s
                lastSdkErrLog = t;
                Log($"iRacing data error (feed restarts itself): {ex.GetType().Name}: {ex.Message}");
            };
            raceState.Start();

            // Pre-warm the transition clips off-thread at startup: pull every frame
            // into the OS file cache and prime the JPEG codec, so the FIRST tower
            // transition is not reading ~180 cold files off disk + initialising the
            // codec mid-playback - the GTP "seizure on first load". Best-effort; the
            // streamer still works if this has not finished yet.
            new Thread(ApexHUD.Native.Overlays.SceneTransitions.WarmUp)
            {
                IsBackground = true,
                Name = "ApexHUD-ClipWarmUp",
            }.Start();

            // Find iRacing's real window; fall back to the primary 1080p lane if
            // the sim isn't up yet (so the host still renders something on screen).
            var target = IRacingWindowFinder.Find();
            int x = target?.X ?? 0, y = target?.Y ?? 0, w = target?.Width ?? 1920, h = target?.Height ?? 1080;
            IntPtr targetHandle = target?.Handle ?? IntPtr.Zero;
            string targetTitle = target?.Title ?? "";
            Log(target is { } t
                ? $"target: 0x{t.Handle:X}  {t.Width}x{t.Height} @ ({t.X},{t.Y})  \"{t.Title}\""
                : "no iRacing window found yet — starting over primary 1920x1080; will attach when it appears");

            // The Phase-C compositor: registry of (renderer, screen-rect, gate),
            // on-demand decision, even-pacing video clock. Renderers reused as-is.
            // Grandstand control app settings (2026-10-23): load before the compositor applies them.
            ControlState.Load();
            ApexHUD.Native.Overlays.SponsorPack.IsOn(ApexHUD.Native.Overlays.Partner.AB);   // reads (or writes the default) sponsors.json now, not mid-race
            // broadcast clock dial (2026-10-24): clock.json + the dials folder, decoded once here, not on a frame
            ApexHUD.Native.Overlays.ClockDial.Log = Log;
            ApexHUD.Native.Overlays.OverlayLog.Sink = Log;   // 2026-10-25: overlay log lines into the log file
            ApexHUD.Native.Overlays.ClockDial.Ensure();
            // custom cards from the card builder (2026-10-24): read once here, then only when the control app saves one
            foreach (var problem in ApexHUD.Native.Overlays.CustomCards.LoadFolder()) Log("custom card skipped: " + problem);
            ApexHUD.Native.Overlays.CustomCards.ReadVar = (name, index) => raceState.ReadVar(name, index);
            using var compositor = new OverlayCompositor(raceState, demo, demoGrid);
            // replay director (2026-10-24): asleep until switched on in the control app
            using var director = new ReplayDirector(raceState);

            // The control app: a tiny local web page (any monitor, any device on this PC).
            // It sleeps until the page asks for something, so it costs the broadcast nothing.
            // highlight reel (2026-10-29, #9): nothing runs until Start is pressed in the control app
            using var reel = new HighlightReelRunner(raceState);
            using var control = new ControlServer(raceState, compositor, director, reel);
            control.Start();
            // StartPhotoBooth(); - retired 2026-10-27 (the livery picture is used instead)
            if (args.Any(a => string.Equals(a, "--control", StringComparison.OrdinalIgnoreCase)))
            {
                try { Process.Start(new ProcessStartInfo(ControlServer.Url) { UseShellExecute = true }); } catch { }
            }

            NativeOverlayHost.CostDetail = compositor.TakeCosts;
            using var host = new NativeOverlayHost(x, y, w, h, compositor.Render, compositor.FrameNeeded,
                onGpuReady: ctx =>
                {
                    GpuFrameUploader.Validate(ctx, out string detail);
                    Log("GPU frame-upload seam: " + detail);
                    IntroWatch.Gpu = ctx;   // the globe's dress rehearsal draws on this card (2026-10-24)
                    ApexHUD.Native.Overlays.GpuContext.Current = ctx;
                    // 2026-10-24 (choppy intro): Skia's default picture memory (~256 MB) was smaller than the globe's
                    // pictures + everything else, so pictures were re-sent to the card every frame. 1.5 GB (12 GB card).
                    ctx.SetResourceCacheLimit(1536L * 1024 * 1024);
                    Log($"GPU picture memory: {ctx.GetResourceCacheLimit() / (1024 * 1024)} MB");
                });
            // 2026-10-27: the TV loading splash (concept A "TRACE") - its own window on the Apollo 4K screen, made when first needed
            using var splashDir = new SplashDirector(raceState);

            // Start hidden unless the sim is already up (or demo mode) - the overlay
            // appears on its own when the sim window shows up, like CrewChief.
            bool simVisible = demo;   // the loop shows it within ~0.25 s once a session is live
            host.SetVisible(simVisible);
            host.Start();
            Log("host started — Ctrl+Shift+Q to quit" + (simVisible ? "" : " (hidden until the sim starts)"));

            var sw = Stopwatch.StartNew();
            long lastWriteMs = 0, lastTrackMs = 0, lastPresent = 0;
            while (host.IsRunning)
            {
                Thread.Sleep(50);

                long nowMs = sw.ElapsedMilliseconds;

                if (autoQuitSeconds > 0 && nowMs >= autoQuitSeconds * 1000L)
                {
                    Log($"auto-quit after {autoQuitSeconds}s");
                    break;
                }

                // Re-track / re-acquire the iRacing window a few times a second so
                // the overlay follows it (and attaches if it appears after launch).
                if (nowMs - lastTrackMs >= 250)
                {
                    lastTrackMs = nowMs;
                    var tracked = targetHandle != IntPtr.Zero
                        ? IRacingWindowFinder.Track(targetHandle, targetTitle)
                        : null;
                    tracked ??= IRacingWindowFinder.Find();
                    // 2026-10-27 (#342, Angelo: "the splash is not getting out of the way... overlays are sitting on my
                    // monitor not on the stream"): once attached to ANY window with "iRacing" in its title (a browser tab
                    // with the member site, the UI) it kept following that window forever, so when the sim's own window
                    // opened the overlays stayed on his monitor and the splash never saw the sim go live. While we're on
                    // something that isn't the sim, look again every tick and move to the sim's window the moment it's there.
                    if (tracked is { } notSim && !notSim.Title.StartsWith("iRacing.com Simulator", StringComparison.Ordinal)
                        && IRacingWindowFinder.Find() is { } better && better.Title.StartsWith("iRacing.com Simulator", StringComparison.Ordinal))
                        tracked = better;
                    if (tracked is { } cur)
                    {
                        if (cur.Handle != targetHandle)
                            Log($"attached: 0x{cur.Handle:X}  {cur.Width}x{cur.Height} @ ({cur.X},{cur.Y})  \"{cur.Title}\"");
                        targetHandle = cur.Handle;
                        targetTitle = cur.Title;
                        host.SetBounds(cur.X, cur.Y, cur.Width, cur.Height);
                    }
                    else if (!demo)
                    {
                        // Sim window gone: re-acquire fresh when it comes back.
                        targetHandle = IntPtr.Zero;
                    }

                    // Show only while the sim window exists AND the session is live
                    // (telemetry flowing) - hidden through loading screens and after
                    // the sim closes, like SimHub/CrewChief.
                    // 2026-10-27: the loading splash on the TV: menus / loading / live, debounced; hand-off to the game
                    splashDir.Tick(nowMs, tracked, demo);
                    bool shouldShow = demo || (tracked != null && raceState.IsSessionLive && raceState.Current.IsConnected
                                               && !splashDir.HoldMainOverlay(nowMs));   // wait for the splash's dissolve (max 6 s)
                    if (shouldShow != simVisible)
                    {
                        simVisible = shouldShow;
                        host.SetVisible(shouldShow);
                        Log(shouldShow ? "session live - overlay shown" : "no live session - overlay hidden");
                    }
                }

                // Every ~5 s: sample the present RATE for the window just elapsed
                // (presents/sec proves the on-demand idle: ~0–2/s between scene
                // rotations, ~60/s during a transition) and roll a fresh report.
                if (nowMs - lastWriteMs >= 5000)
                {
                    long present = host.PresentedFrames;
                    double secs = (nowMs - lastWriteMs) / 1000.0;
                    double pps = secs > 0 ? (present - lastPresent) / secs : 0;
                    RecordWindow(pps);
                    lastWriteMs = nowMs;
                    lastPresent = present;
                    WriteReport(host.CopyGaps(), host.PresentedFrames, sw.Elapsed.TotalSeconds,
                                running: true, target: targetTitle);
                }
            }

            host.Stop();
            raceState.Stop();
            compositor.Dispose();

            WriteReport(host.CopyGaps(), host.PresentedFrames, sw.Elapsed.TotalSeconds,
                        running: false, target: targetTitle);
            Log(host.QuitRequested ? "quit (Ctrl+Shift+Q)" : "quit");
            return 0;
        }

        // Event log buffered in memory so the rolling report (which rewrites the
        // file) never clobbers a startup line or a FATAL.
        static readonly object logLock = new();
        static readonly List<string> events = new();
        static readonly List<double> windowPps = new();
        static string lastReport = "";

        internal static void Log(string msg)
        {
            lock (logLock)
            {
                events.Add($"{DateTime.Now:HH:mm:ss.fff}  {msg}");
                if (events.Count > 3000) events.RemoveRange(0, 1000);   // a long session never grows the log forever
                logDirty = true;
            }
            // 2026-10-30 (#604): the once-a-second save used to write the file while holding the log lock, so a frame
            // that logged something at that moment waited for the disk. Now the text is put together under the lock
            // and written after it is released (an older text never overwrites a newer one).
            logTimer ??= new System.Threading.Timer(_ =>
            {
                string text; long seq;
                lock (logLock) { if (!logDirty) return; logDirty = false; text = BuildLocked(); seq = ++logSeq; }
                WriteLog(text, seq);
            }, null, 1000, 1000);
        }
        static bool logDirty;
        static System.Threading.Timer? logTimer;

        internal static void LogFatal(string msg)
        {
            lock (logLock)
            {
                events.Add($"FATAL: {msg}");
                FlushLocked();
            }
        }

        static void RecordWindow(double pps)
        {
            lock (logLock) windowPps.Add(pps);
        }

        static long logSeq, logWritten;
        static readonly object logWriteLock = new();

        static string BuildLocked()
        {
            var sb = new StringBuilder();
            sb.Append(lastReport);
            sb.AppendLine("# --- event log ---");
            foreach (var e in events) sb.AppendLine(e);
            return sb.ToString();
        }

        static void WriteLog(string text, long seq)
        {
            lock (logWriteLock)
            {
                if (seq <= logWritten) return;
                logWritten = seq;
                try { File.WriteAllText(LogPath, text); } catch { }
            }
        }

        static void FlushLocked()
        {
            WriteLog(BuildLocked(), ++logSeq);
        }

        static void WriteReport(double[] gaps, long presents, double elapsedSec, bool running, string target)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Grandstand GPU host (Phase C) — report  ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
            sb.AppendLine($"# status : {(running ? "RUNNING" : "stopped")}");
            sb.AppendLine($"# target : {(string.IsNullOrEmpty(target) ? "(none)" : target)}");
            sb.AppendLine();

            // ===== ON-DEMAND: present RATE per 5 s window =====
            // The whole point of Phase C's on-demand loop: presents/sec should fall
            // to near-zero when the overlays are static and spike only during a
            // transition/animation. A flat ~60/s here would mean on-demand failed.
            double avgRate = elapsedSec > 0 ? presents / elapsedSec : 0;
            sb.AppendLine("# ===== ON-DEMAND PRESENT RATE (presents/sec per 5s window) =====");
            sb.AppendLine($"# total presents : {presents} over {elapsedSec:F0}s  => {avgRate:F1}/s average (incl. idle)");
            lock (logLock)
            {
                if (windowPps.Count == 0)
                {
                    sb.AppendLine("# (no full window sampled yet)");
                }
                else
                {
                    int from = Math.Max(0, windowPps.Count - 16);
                    var recent = new StringBuilder("# recent windows:");
                    for (int i = from; i < windowPps.Count; i++)
                        recent.Append($" {windowPps[i]:F0}");
                    sb.AppendLine(recent.ToString());
                    sb.AppendLine("# near-0 => idle (static overlays, ~0 GPU); ~30-60 => bursting on a transition/animation.");
                }
            }
            sb.AppendLine();

            // ===== BURST CADENCE: gaps between presents while bursting =====
            // Idle gaps (multi-second) are filtered out so this measures the smooth
            // present quality WHILE rendering. A handful of huge gaps is expected
            // and itself proves the host idled between bursts.
            var burst = gaps.Where(g => g <= 100.0).ToArray();
            int idleGaps = gaps.Length - burst.Length;
            if (burst.Length < 2)
            {
                sb.AppendLine($"# burst frames: {burst.Length} — not enough presented frames yet.");
            }
            else
            {
                var sorted = (double[])burst.Clone();
                Array.Sort(sorted);
                double avg = burst.Average();
                int over33 = burst.Count(g => g > 33.0), over20 = burst.Count(g => g > 20.0);
                sb.AppendLine("# ===== BURST CADENCE (ms between presents, idle gaps excluded) =====");
                sb.AppendLine($"# burst frames : {burst.Length}   (idle gaps >100ms: {idleGaps})");
                sb.AppendLine($"# avg    : {avg:F2} ms   (=> {1000.0 / avg:F1} fps while bursting)");
                sb.AppendLine($"# median : {sorted[sorted.Length / 2]:F2} ms");
                sb.AppendLine($"# p95    : {sorted[(int)(sorted.Length * 0.95)]:F2} ms");
                sb.AppendLine($"# min/max: {sorted[0]:F2} / {sorted[^1]:F2} ms");
                sb.AppendLine($"# >33ms  : {over33} ({100.0 * over33 / burst.Length:F1}%)   >20ms: {over20} ({100.0 * over20 / burst.Length:F1}%)");
                sb.AppendLine();
                sb.AppendLine("# The headline metric is the PRESENT RATE above (idle gaps prove ~0 GPU at rest).");
                sb.AppendLine("# Burst cadence depends on how many overlays co-draw on the single thread: tower+splash");
                sb.AppendLine("# alone held ~60fps (Phase B); adding the heavy featured card serialises its per-frame");
                sb.AppendLine("# cost, so a multi-overlay burst runs ~30-45fps. Caching static overlays to a layer");
                sb.AppendLine("# (requirement B) is the path back to 60fps as more overlays migrate — see PORT_PLAN.md.");
            }
            sb.AppendLine();

            string text; long seq;
            lock (logLock)
            {
                lastReport = sb.ToString();
                text = BuildLocked(); seq = ++logSeq;   // written below, outside the lock (#604)
            }
            WriteLog(text, seq);
        }

        /// <summary>2026-10-25: the photo booth (real-lighting front photos of every starter, taken on a replay's
        /// formation lap) - a small Python helper; started here when it's installed, one copy at a time (it checks).</summary>
        static void StartPhotoBooth()
        {
            try
            {
                string py = @"C:\SimRacing\Tools\photo_venv\Scripts\pythonw.exe", script = @"C:\SimRacing\Tools\photo_booth.py";
                if (!File.Exists(py) || !File.Exists(script)) return;
                Process.Start(new ProcessStartInfo(py, $"\"{script}\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(script)! });
                Log("photo booth started");
            }
            catch (Exception ex) { Log("photo booth: " + ex.Message); }
        }
    }
}
