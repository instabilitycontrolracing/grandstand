using ApexHUD.Native.Common;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using ApexHUD.Native.Data;
using ApexHUD.Native.Overlays;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// The Grandstand control app's back end (2026-10-23): a tiny web server on this PC
/// (http://127.0.0.1:8790) that serves the control page and a small JSON API.
///
/// <para><b>Costs the broadcast nothing.</b> It runs on its own background thread and
/// blocks waiting for a request; with no control page open it does no work at all.
/// It never touches the render loop directly: settings go through
/// <see cref="ControlState"/> (version-checked once per tick) and one-shot requests
/// (test cards) through a queue the render thread drains.</para>
/// </summary>
internal sealed class ControlServer : IDisposable
{
    public const int Port = 8790;
    public static string Url => $"http://127.0.0.1:{Port}/";

    readonly RaceStateService raceState;
    readonly OverlayCompositor compositor;
    readonly ReplayDirector? director;
    readonly HighlightReelRunner? reel;
    readonly HttpListener listener = new();
    Thread? thread;
    volatile bool running;

    static readonly string WebRoot = Path.Combine(AppContext.BaseDirectory, "control");
    static readonly string AssetsRoot = Path.Combine(AppContext.BaseDirectory, "assets");
    static readonly HashSet<string> Documents = new(StringComparer.Ordinal) { "eventpack", "cards", "transitions" };

    public ControlServer(RaceStateService raceState, OverlayCompositor compositor, ReplayDirector? director = null, HighlightReelRunner? reel = null)
    {
        this.reel = reel;
        this.raceState = raceState;
        this.compositor = compositor;
        this.director = director;
        // 2026-11-06 (remote control): with "Phones and laptops on my network" on, Grandstand also answers on this PC's
        // network address - every request from another device needs the PIN (Handle). Takes effect at the next start.
        if (ProducerDesk.Current.LanOn) listener.Prefixes.Add($"http://+:{Port}/");
        else
        {
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Prefixes.Add($"http://localhost:{Port}/");
        }
    }

    public bool Start()
    {
        try { listener.Start(); }
        catch (Exception ex) { Program.Log("control app: could not start (" + ex.Message + ")"); return false; }
        running = true;
        thread = new Thread(Loop) { IsBackground = true, Name = "Grandstand-Control", Priority = ThreadPriority.BelowNormal };
        thread.Start();
        Program.Log("control app ready at " + Url);
        return true;
    }

    void Loop()
    {
        while (running)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch { if (!running) return; continue; }
            try { Handle(ctx); }
            catch (Exception ex)
            {
                try { Send(ctx, 500, "text/plain", Encoding.UTF8.GetBytes(ex.Message)); } catch { }
            }
        }
    }

    void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        string path = req.Url?.AbsolutePath ?? "/";
        string method = req.HttpMethod;

        // 2026-11-06 (remote control): another device on the network needs the PIN - once in the link (?pin=1234), then
        // a cookie. Nothing from outside this PC gets in without it.
        if (req.RemoteEndPoint != null && !System.Net.IPAddress.IsLoopback(req.RemoteEndPoint.Address))
        {
            string pin = ProducerDesk.Current.LanPin;
            bool ok = ProducerDesk.Current.LanOn && pin.Length >= 4 && (req.QueryString["pin"] == pin || req.Cookies["gs_pin"]?.Value == pin);
            if (!ok) { Send(ctx, 403, "text/plain", Encoding.UTF8.GetBytes("Grandstand: open the link with the PIN shown on the Producer page (Remote control).")); return; }
            if (req.QueryString["pin"] == pin) ctx.Response.Headers.Add("Set-Cookie", $"gs_pin={pin}; Path=/; Max-Age=2592000; SameSite=Strict");
        }
        // Changes need our own header: a web page open in the browser can't add it to a cross-site
        // request, so no website can blank or hijack the broadcast (CSRF).
        if (method != "GET" && req.Headers["X-Grandstand"] != "1")
        { Send(ctx, 403, "text/plain", Encoding.UTF8.GetBytes("forbidden")); return; }

        if (path == "/api/state" && method == "GET") { SendJson(ctx, compositor.Describe(raceState)); return; }
        if (path == "/api/director/scan" && method == "POST") { ReplayDirector.ScanRequested = true; SendJson(ctx, new { ok = true }); return; }   // 2026-11-06 (#859)
            if (path == "/api/settings" && method == "GET") { SendJson(ctx, ControlState.Current); return; }
        if (path == "/api/settings" && method == "POST")
        {
            // 2026-11-06 (#890): merge onto the current settings - a POST with a few settings used to reset every other one
            // to its default (and save that), which is why spout, the director, themes, toggles kept "forgetting" themselves
            ControlSettings? s;
            try
            {
                var cur = System.Text.Json.JsonSerializer.SerializeToNode(ControlState.Current, ControlState.Json)!.AsObject();
                var patchObj = System.Text.Json.Nodes.JsonNode.Parse(ReadBody(req))!.AsObject();
                foreach (var kv in patchObj) cur[kv.Key] = kv.Value?.DeepClone();
                s = cur.Deserialize<ControlSettings>(ControlState.Json);
            }
            catch { s = null; }
            if (s == null) { Send(ctx, 400, "text/plain", Encoding.UTF8.GetBytes("bad settings")); return; }
            ControlState.Replace(s);
            SendJson(ctx, ControlState.Current);
            return;
        }
        // 2026-10-30 (#601): themes - the presets and custom themes for the editor, and its live preview (never saved;
        // custom themes are saved through /api/settings like everything else: customThemes in control.json)
        if (path == "/api/themes" && method == "GET")
        {
            var cur = ControlState.Current;
            SendJson(ctx, new
            {
                builtIns = ApexHUD.Native.Overlays.Theme.BuiltIns.Select(t => t.ToSpec()).ToList(),
                customs = cur.CustomThemes,
                picked = cur.Theme,
                onAir = ApexHUD.Native.Overlays.Theme.Active.Id,
                previewing = ApexHUD.Native.Overlays.Theme.Previewing,
                textures = ApexHUD.Native.Overlays.Theme.TextureKeys, motifs = ApexHUD.Native.Overlays.Theme.MotifKeys,
                particles = ApexHUD.Native.Overlays.Theme.ParticleKeys, targets = ApexHUD.Native.Overlays.Theme.TargetKeys,
            });
            return;
        }
        if (path == "/api/theme/preview" && method == "POST")
        {
            var spec = JsonSerializer.Deserialize<ApexHUD.Native.Overlays.ThemeSpec>(ReadBody(req), ControlState.Json);
            if (spec == null) { Send(ctx, 400, "text/plain", Encoding.UTF8.GetBytes("bad theme")); return; }
            ControlState.SetPreview(spec);
            SendJson(ctx, new { previewing = true });
            return;
        }
        if (path == "/api/theme/preview/stop" && method == "POST") { ControlState.SetPreview(null); SendJson(ctx, new { previewing = false }); return; }
        // 2026-10-28 (#501): OBS status for the stream deck's side panel (a plugin page in OpenDeck reads it - CORS open, read-only)
        if (path == "/api/obs" && method == "GET")
        {
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
            var st = ObsMarkers.Status;
            bool fresh = (DateTime.UtcNow - st.At).TotalSeconds < 8;
            SendJson(ctx, new { linked = st.Linked && fresh, scene = st.Scene, streaming = st.Streaming && fresh, recording = st.Recording && fresh, buffer = st.Buffer && fresh });
            return;
        }
        // 2026-10-30 (#611): the cam placer - what it sees in OBS, and a test (a pretend graphic over the face cam for 6 s)
        if (path == "/api/cams" && method == "GET") { SendJson(ctx, ObsCams.Describe()); return; }
        if (path == "/api/cams/test" && method == "POST") { SendJson(ctx, new { result = ObsCams.Test() }); return; }
        // 2026-10-28 (#499): "flag this moment" - the Engineer's replay remote (X on the controller) or a stream-deck key
        if (path == "/api/highlight" && method == "POST")
        {
            string label = "Flagged moment";
            try
            {
                using var doc = JsonDocument.Parse(ReadBody(req));
                if (doc.RootElement.TryGetProperty("label", out var l) && l.GetString() is { Length: > 0 and < 80 } t) label = t;
            }
            catch { }
            ApexHUD.Native.Overlays.HighlightLog.Add("flag", label, true);
            SendJson(ctx, new { flagged = label });
            return;
        }
        // 2026-10-29 (#494): viewer interactions from Streamer.bot / StreamElements on this PC (no account logins here):
        // {"type":"superchat|tip|bits|member|sub|follow","name":"...","amount":"$5.00","message":"...","platform":"youtube|twitch|tip"}
        if (path.StartsWith("/api/viewer", StringComparison.Ordinal)) { Viewer(ctx, path, method); return; }
        if (path == "/api/test" && method == "POST")
        {
            string card = req.QueryString["card"] ?? "";
            if (card.Length is > 0 and < 40) ControlState.Requests.Enqueue(card);
            SendJson(ctx, new { queued = card });
            return;
        }
        if (path == "/api/radio" && method == "POST")
        {
            // team radio caption from the race engineer app: {"who": "ENGINEER", "text": "..."}
            try
            {
                using var doc = JsonDocument.Parse(ReadBody(req));
                string who = doc.RootElement.TryGetProperty("who", out var w) ? w.GetString() ?? "" : "";
                string text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                if (text.Length is > 0 and < 700 && who.Length < 40) ApexHUD.Native.Overlays.RadioFeed.Push(who, text);
                SendJson(ctx, new { ok = true });
            }
            catch { Send(ctx, 400, "text/plain", Encoding.UTF8.GetBytes("bad radio")); }
            return;
        }
        if (path == "/api/telemetry" && method == "GET") { SendJson(ctx, Cached(ref telemetryCache, () => raceState.ListTelemetry())); return; }
        if (path == "/api/sessioninfo" && method == "GET") { SendJson(ctx, Cached(ref sessionCache, () => raceState.ListSessionInfo())); return; }
        // 2026-10-25 (Angelo: the iPad live map "reflect the themes and stuff for the corners and straights"): every
        // named stop of the lap with its theme, and each stop's themed label as a picture, for the engineer app's iPad page
        if (path == "/api/places" && method == "GET")
        {
            var snap = raceState.Current;
            var stops = TrackCorners.Lap(snap.TrackRawName, snap.Extras.TrackLength) ?? new();
            var lc = PreRaceCards.TrackLogoColour(snap);
            SendJson(ctx, new
            {
                track = snap.TrackRawName,
                bg = lc is { } bgc ? $"#{bgc.Red:X2}{bgc.Green:X2}{bgc.Blue:X2}" : "",
                stops = stops.Select(st =>
                {
                    var th = CornerTheme.For(st.Name);
                    string Hex(SkiaSharp.SKColor c2) => $"#{c2.Red:X2}{c2.Green:X2}{c2.Blue:X2}";
                    return new { name = st.Name, s0 = st.StartPct, s1 = st.EndPct, straight = st.Straight,
                                 kind = th?.Kind.ToString() ?? "", eye = th != null ? Hex(th.Eye) : "", rule = th != null ? Hex(th.Rule) : "",
                                 heritage = CornerTheme.HeritageOf(snap.TrackRawName, st.Name) };
                }).ToArray(),
            });
            return;
        }
        if (path == "/api/placelabel" && method == "GET")
        {
            string nm = req.QueryString["name"] ?? "", eye = req.QueryString["eye"] ?? "INTO";
            var img = nm.Length > 0 ? CornerTheme.LabelImage(eye.ToUpperInvariant(), nm.ToUpperInvariant()) : null;
            if (img == null) { Send(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("no label")); return; }
            using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            Send(ctx, 200, "image/png", data.ToArray());
            return;
        }
        if (path == "/api/director" && method == "GET")
        { SendJson(ctx, director?.Describe() ?? new { enabled = false, status = "Unavailable", why = "Director not running", groups = Array.Empty<string>() }); return; }
        // 2026-10-29 (#9): the highlight reel - GET status (+ ?sessions=1 the picker), POST start {file} / stop
        if (path == "/api/reel" && method == "GET")
        {
            object? sessions = req.QueryString["sessions"] == "1"
                ? HighlightReelRunner.Sessions().Select(x => new { file = x.File, label = x.Label, markers = x.Markers, moments = x.Moments, seconds = Math.Round(x.Seconds), written = x.Written.ToString("yyyy-MM-dd HH:mm") }).ToArray()
                : null;
            SendJson(ctx, new { reel = reel?.Describe(), sessions });
            return;
        }
        if (path == "/api/reel/start" && method == "POST")
        {
            if (reel == null) { SendJson(ctx, new { started = false, msg = "The highlight reel isn't available." }); return; }
            string? file = null;
            try
            {
                string body = ReadBody(req);
                if (body.Trim().Length > 0)
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String) file = f.GetString();
                }
            }
            catch { }
            bool ok = reel.Start(file, out var msg);
            SendJson(ctx, new { started = ok, msg, reel = reel.Describe() });
            return;
        }
        if (path == "/api/reel/stop" && method == "POST")
        {
            reel?.Stop();
            SendJson(ctx, new { stopped = true, reel = reel?.Describe() });
            return;
        }
        if (path.StartsWith("/api/sponsors", StringComparison.Ordinal)) { Sponsors(ctx, path, method); return; }
        if (path.StartsWith("/api/dial", StringComparison.Ordinal)) { Dials(ctx, path, method); return; }
        if (path.StartsWith("/api/cardimage/", StringComparison.Ordinal)) { CardImage(ctx, path, method); return; }
        // 2026-11-05 (Angelo: "a viewer that shows the overlay... click any part... see the changes instantly"): the live view
        if (path == "/api/live/frame" && method == "GET")
        {
            int w = int.TryParse(req.QueryString["w"], out int qw) ? Math.Clamp(qw, 480, 1920) : 1280;
            Interlocked.Exchange(ref OverlayCompositor.SampleUntilTicks, Environment.TickCount64 + 3000);   // the sample race runs while the Live view is open
            var (jpg, _, live) = CardPreview.Request("frame:" + w, 2500);
            if (jpg == null) { Text(ctx, 503, "Grandstand is busy - try again"); return; }
            ctx.Response.Headers["X-Live"] = live ? "1" : "0";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            Send(ctx, 200, "image/jpeg", jpg);
            return;
        }
        if (path == "/api/datacatalog" && method == "GET") { SendJson(ctx, DataCatalog.Build(raceState, compositor.CurrentSnapshot)); return; }   // 2026-11-05: the card builder's data picker
        if (path.StartsWith("/api/integrations", StringComparison.Ordinal)) { IntegrationsApi(ctx, path, method); return; }   // 2026-11-05
        // 2026-11-06: chat commands - Streamer.bot's "Fetch URL" (a plain GET) gets the reply text to send to chat
        if (path == "/api/chat" && method == "GET")
        {
            string reply = ChatCommands.Answer(raceState.Current, req.QueryString["cmd"] ?? "", req.QueryString["user"] ?? "", req.QueryString["input"] ?? "");
            Send(ctx, 200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(reply));
            return;
        }
        if (path.StartsWith("/api/do/", StringComparison.Ordinal) && method == "POST") { DoApi(ctx, path); return; }        // Stream Deck keys
        if (path.StartsWith("/api/desk", StringComparison.Ordinal)) { DeskApi(ctx, path, method); return; }               // 2026-11-06: producer desk
        if (path == "/api/booth" && method == "GET") { SendJson(ctx, ProducerDesk.Booth()); return; }                      // 2026-11-06: commentator page
        // 2026-11-06: the Live view plays the sample race's sound inside the app (LiveSound.cs)
        if (path == "/api/live/sounds" && method == "GET") { SendJson(ctx, LiveSound.Since(int.TryParse(req.QueryString["since"], out int sn) ? sn : 0)); return; }
        if (path.StartsWith("/api/live/sound/", StringComparison.Ordinal) && method == "GET")
        {
            var f = int.TryParse(path["/api/live/sound/".Length..], out int sq) ? LiveSound.File(sq) : null;
            if (f == null) { Text(ctx, 404, "gone"); return; }
            ctx.Response.Headers["Cache-Control"] = "max-age=600";
            Send(ctx, 200, f.Value.Type, f.Value.Data);
            return;
        }
        // 2026-11-06: the app's top bar mirrors the session bar - the real watch, and the bar's own facts
        if (path == "/api/watch.png" && method == "GET")
        {
            int sz = int.TryParse(req.QueryString["s"], out int qs) ? qs : 160;
            var (png, _, _) = CardPreview.Request("watch:" + Math.Clamp(sz, 64, 320), 1500);
            if (png == null) { Text(ctx, 503, "busy"); return; }
            ctx.Response.Headers["Cache-Control"] = "no-store";
            Send(ctx, 200, "image/png", png);
            return;
        }
        if (path == "/api/bar" && method == "GET") { SendJson(ctx, BarInfo()); return; }
        // 2026-11-06: vertical clips (VerticalClip.cs)
        if (path == "/api/clip" && method == "POST") { SendJson(ctx, new { message = VerticalClip.Request(req.QueryString["by"] ?? "button"), clip = VerticalClip.StatusObject() }); return; }
        if (path == "/api/clip" && method == "GET") { SendJson(ctx, VerticalClip.StatusObject()); return; }
        if (path == "/api/clip/preview.png" && method == "GET")
        {
            Interlocked.Exchange(ref OverlayCompositor.SampleUntilTicks, Environment.TickCount64 + 3000);
            var (jpg, _, _) = CardPreview.Request("frame:1280", 2500);
            var png = jpg == null ? null : VerticalClip.Preview(jpg);
            if (png == null) { Text(ctx, 503, "busy"); return; }
            ctx.Response.Headers["Cache-Control"] = "max-age=3600";   // the page asks for a new ?t= on Refresh; repaints reuse it
            Send(ctx, 200, "image/png", png);
            return;
        }
        if (path == "/api/clip/ffmpeg" && method == "POST") { SendJson(ctx, new { message = VerticalClip.GetFfmpeg() }); return; }
        if (path == "/api/clip/folder" && method == "POST")
        {
            Directory.CreateDirectory(VerticalClip.Folder);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + VerticalClip.Folder + "\"") { UseShellExecute = true }); } catch { }
            SendJson(ctx, new { ok = true }); return;
        }
        if (path == "/api/clip/open" && method == "POST")
        {
            var p = VerticalClip.PathOf(req.QueryString["name"] ?? "");
            if (p == null) { Text(ctx, 404, "no such clip"); return; }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + p + "\"") { UseShellExecute = true }); } catch { }
            SendJson(ctx, new { ok = true }); return;
        }
        if (path == "/api/health" && method == "GET") { SendJson(ctx, AppHealth.Describe()); return; }                     // 2026-11-06: updates, crash reports, setup
        if (path == "/api/health/wizard" && method == "POST") { AppHealth.WizardDone(); SendJson(ctx, new { ok = true }); return; }
        if (path == "/api/live/boxes" && method == "GET")
        {
            SendJson(ctx, new
            {
                live = OverlayCompositor.LiveIsLive, offAir = OverlayCompositor.LiveOffAir,
                sample = OverlayCompositor.SampleRunning, stage = OverlayCompositor.SampleStage, sound = OverlayCompositor.SampleSound,
                boxes = OverlayCompositor.LiveBoxes.Select(b => new { id = b.Id, x = b.X, y = b.Y, w = b.W, h = b.H, drawn = b.Drawn }),
            });
            return;
        }
        if (path == "/api/live/sample" && method == "POST")   // the sample race: pre-race show / race / finish, "restart" plays it from the top
        {
            string st = req.QueryString["stage"] ?? OverlayCompositor.SampleStage;
            if (st is not ("prerace" or "race" or "finish")) { Text(ctx, 400, "unknown stage"); return; }
            OverlayCompositor.SampleStage = st;
            if (req.QueryString["restart"] == "1") OverlayCompositor.SampleRun++;
            if (req.QueryString["sound"] is string snd) OverlayCompositor.SampleSound = snd != "0";
            Interlocked.Exchange(ref OverlayCompositor.SampleUntilTicks, Environment.TickCount64 + 3000);
            SendJson(ctx, new { stage = st, run = OverlayCompositor.SampleRun, sound = OverlayCompositor.SampleSound });
            return;
        }
        if (path == "/api/live/trigger" && method == "POST")
        {
            string what = req.QueryString["what"] ?? "";
            if (what == "radio") RadioFeed.Push("ENGINEER", "Good pace. Box this lap, box this lap - we're going to four tyres.");
            else if (what == "spotter") RadioFeed.Push("SPOTTER", "Car inside, car inside. Still there... clear.");
            else { Text(ctx, 400, "unknown moment"); return; }
            SendJson(ctx, new { played = what });
            return;
        }
        // 2026-11-05: the card editor's live preview - the render thread draws the graphic and we send the picture
        if (path.StartsWith("/api/preview/", StringComparison.Ordinal) && method == "GET")
        {
            string id = path["/api/preview/".Length..];
            if (!CardPictures.TryRect(id, out _)) { Text(ctx, 404, "no such card"); return; }
            var (png, empty, live) = CardPreview.Request(id);
            if (png == null) { Text(ctx, 503, "Grandstand is busy - try again"); return; }
            ctx.Response.Headers["X-Preview-Empty"] = empty ? "1" : "0";
            ctx.Response.Headers["X-Preview-Live"] = live ? "1" : "0";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            Send(ctx, 200, "image/png", png);
            return;
        }
        // class transitions (2026-10-24): which source plays per class; POST rescan after dropping frames in
        if (path == "/api/transitions" && method == "GET") { SendJson(ctx, SceneTransitions.Describe()); return; }
        if (path == "/api/transitions/rescan" && method == "POST") { SceneTransitions.Rescan(); SendJson(ctx, SceneTransitions.Describe()); return; }
        // 2026-10-26 (#39): record your own roller - the next few seconds of the iRacing window (replay) become a class's transition
        if (path == "/api/roller" && method == "POST")
        {
            string cls = (req.QueryString["cls"] ?? "").ToLowerInvariant();
            if (!SceneTransitions.UserKeys.Contains(cls)) { Send(ctx, 400, "text/plain", Encoding.UTF8.GetBytes("unknown class")); return; }
            double.TryParse(req.QueryString["sec"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sec);
            sec = sec <= 0 ? 3 : Math.Clamp(sec, 1, 8);
            string py = @"C:\SimRacing\Tools\photo_venv\Scripts\pythonw.exe", script = @"C:\SimRacing\Tools\roller_recorder.py";
            if (!File.Exists(py) || !File.Exists(script)) { SendJson(ctx, new { started = false, msg = "the roller recorder isn't installed on this PC" }); return; }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(py,
                    $"\"{script}\" --cls {cls} --sec {sec.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
                    { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(script)! });
                Program.Log($"roller: recording {sec:F1} s for {cls}");
                SendJson(ctx, new { started = true, cls, sec });
            }
            catch (Exception ex) { SendJson(ctx, new { started = false, msg = ex.Message }); }
            return;
        }
        if (path == "/api/roller" && method == "GET")
        {
            string f = Path.Combine(SceneTransitions.UserFolder, "_roller_status.json");
            string body = "{}";
            try { if (File.Exists(f)) body = File.ReadAllText(f); } catch { }
            Send(ctx, 200, "application/json", Encoding.UTF8.GetBytes(body));
            return;
        }
        if (path.StartsWith("/api/cards", StringComparison.Ordinal)) { Cards(ctx, path, method); return; }
        if (path.StartsWith("/api/packs", StringComparison.Ordinal)) { Packs(ctx, path, method); return; }
        if (path.StartsWith("/api/doc/", StringComparison.Ordinal))
        {
            string name = path["/api/doc/".Length..];
            if (!Documents.Contains(name)) { Send(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("unknown document")); return; }
            string file = Path.Combine(ControlState.Folder, name + ".json");
            if (method == "GET")
            {
                byte[] b = File.Exists(file) ? File.ReadAllBytes(file) : Encoding.UTF8.GetBytes("null");
                Send(ctx, 200, "application/json", b);
                return;
            }
            if (method == "PUT" || method == "POST")
            {
                string body = ReadBody(req);
                using (JsonDocument.Parse(body)) { }   // only valid JSON is saved
                Directory.CreateDirectory(ControlState.Folder);
                File.WriteAllText(file, body);
                SendJson(ctx, new { saved = name });
                return;
            }
        }

        // ---- static files: the control page and the overlay fonts ----
        if (method != "GET") { Send(ctx, 405, "text/plain", Array.Empty<byte>()); return; }
        string root = WebRoot, rel;
        if (path.StartsWith("/assets/fonts/", StringComparison.Ordinal) || path.StartsWith("/assets/flags/", StringComparison.Ordinal)) { root = AssetsRoot; rel = path["/assets/".Length..]; }   // flags: the top bar (2026-11-06)
        else rel = path == "/" ? "index.html" : path.TrimStart('/');
        string full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        { Send(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("not found")); return; }
        Send(ctx, 200, ContentType(full), File.ReadAllBytes(full));
    }

    // ---------------------------------------------------------------- 2026-10-24 additions

    // The data browser polls at most a few times a second; several open tabs share one read.
    sealed record CacheBox(long AtMs, object Value);
    CacheBox? telemetryCache, sessionCache;
    static object Cached(ref CacheBox? box, Func<object> make)
    {
        var b = box;
        long now = Environment.TickCount64;
        if (b != null && now - b.AtMs < 250) return b.Value;
        var v = make();
        box = new CacheBox(now, v);
        return v;
    }

    /// <summary>Safe file name for a card or pack: letters, digits, '-' and '_' only, 1-40 characters.</summary>
    static string? SafeId(string raw)
    {
        raw = Uri.UnescapeDataString(raw).Trim();
        if (raw.Length is 0 or > 40) return null;
        foreach (char c in raw) if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
        return raw;
    }

    static void Text(HttpListenerContext ctx, int status, string msg) => Send(ctx, status, "text/plain", Encoding.UTF8.GetBytes(msg));

    void Sponsors(HttpListenerContext ctx, string path, string method)
    {
        if (path == "/api/sponsors" && method == "GET")
        {
            var o = new System.Text.Json.Nodes.JsonObject
            {
                ["pack"] = SponsorPack.Export(),
                ["slots"] = new System.Text.Json.Nodes.JsonArray(SponsorPack.Slots.Select(x => (System.Text.Json.Nodes.JsonNode?)x).ToArray()),
                ["file"] = SponsorPack.FilePath,
            };
            Send(ctx, 200, "application/json", Encoding.UTF8.GetBytes(o.ToJsonString()));
            return;
        }
        if (path == "/api/sponsors" && method is "PUT" or "POST")
        {
            try { SponsorPack.Save(ReadBody(ctx.Request)); }
            catch (Exception ex) { Text(ctx, 400, "bad sponsor pack: " + ex.Message); return; }
            Send(ctx, 200, "application/json", Encoding.UTF8.GetBytes(SponsorPack.Export().ToJsonString()));
            return;
        }
        if (path == "/api/sponsors/reset" && method == "POST")
        {
            SponsorPack.Reset();
            Send(ctx, 200, "application/json", Encoding.UTF8.GetBytes(SponsorPack.Export().ToJsonString()));
            return;
        }
        Text(ctx, 404, "not found");
    }

    // ---- broadcast clock dial (2026-10-24): ClockDial picks, decodes and swaps; we only list and choose ----
    static readonly Dictionary<string, string> BuiltInDialNames = new(StringComparer.Ordinal)
    {
        ["homage_black"] = "Black", ["homage_green"] = "Green", ["homage_ivory"] = "Ivory",
        ["homage_navy"] = "Navy", ["homage_panda"] = "Panda",
    };

    static object DescribeDials()
    {
        var f = ClockDial.Current;
        var builtIns = new List<object>();
        if (ClockDial.HasClassic()) builtIns.Add(new { id = "classic", name = "Classic", thumb = "/api/dials/thumb/classic" });
        foreach (var id in ClockDial.BuiltIns)
            if (File.Exists(ClockDial.BuiltInPath(id)))
                builtIns.Add(new { id, name = BuiltInDialNames.GetValueOrDefault(id, id), thumb = "/api/dials/thumb/builtin/" + id });
        var custom = ClockDial.ListCustom().Select(n => new { id = "file:" + n, name = n, thumb = "/api/dials/thumb/file/" + Uri.EscapeDataString(n) }).ToList();
        return new
        {
            choice = f.Choice, active = f.Active, subdialHands = ClockDial.SavedSubdialHands, showingSubdialHands = f.SubdialHands,
            defaultDial = ClockDial.HasClassic() ? "classic" : ClockDial.DefaultBuiltIn,
            folder = ClockDial.DialsFolder, builtIns, custom,
        };
    }

    // ---- 2026-11-05: uploads from the card editor (card pictures) and the clock panel (your own watch face) ----
    static byte[]? ReadUpload(HttpListenerRequest req, long max)
    {
        if (req.ContentLength64 > max) return null;
        using var ms = new MemoryStream();
        var buf = new byte[81920]; int n;
        while ((n = req.InputStream.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > max) return null; }
        return ms.ToArray();
    }
    static string? ImageExt(byte[] b)
    {
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8) return ".jpg";
        if (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return ".webp";
        return null;
    }
    static bool Decodes(byte[] b) { try { using var bmp = SkiaSharp.SKBitmap.Decode(b); return bmp != null && bmp.Width > 0; } catch { return false; } }

    void CardImage(HttpListenerContext ctx, string path, string method)
    {
        string id = path["/api/cardimage/".Length..];
        if (id.Length == 0 || id.Length > 24 || !id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_') || !CardPictures.TryRect(id, out _))
        { Text(ctx, 404, "no such card"); return; }
        var cur = ControlState.Current;
        cur.Elements.TryGetValue(id, out var es);
        if (method == "GET")
        {
            var p = es != null ? CardPictures.PathOf(es.Image) : null;
            if (p == null || !File.Exists(p)) { Text(ctx, 404, "no picture"); return; }
            Send(ctx, 200, ContentType(p), File.ReadAllBytes(p));
            return;
        }
        if (method is "POST" or "PUT")
        {
            var bytes = ReadUpload(ctx.Request, 25L * 1024 * 1024);
            if (bytes == null) { Text(ctx, 413, "That picture is too big (25 MB max)."); return; }
            var ext = ImageExt(bytes);
            if (ext == null || !Decodes(bytes)) { Text(ctx, 400, "That file isn't a picture Grandstand can read (PNG, JPG or WebP)."); return; }
            Directory.CreateDirectory(CardPictures.Folder);
            string file = $"{id}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
            File.WriteAllBytes(Path.Combine(CardPictures.Folder, file), bytes);
            foreach (var old in Directory.GetFiles(CardPictures.Folder, id + "_*"))
                if (!string.Equals(Path.GetFileName(old), file, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch { }
            UpdateElement(id, e => e.Image = file);
            SendJson(ctx, ControlState.Current);
            return;
        }
        if (method == "DELETE")
        {
            if (es != null && CardPictures.PathOf(es.Image) is { } p && File.Exists(p)) try { File.Delete(p); } catch { }
            UpdateElement(id, e => e.Image = "");
            SendJson(ctx, ControlState.Current);
            return;
        }
        Text(ctx, 405, "method not allowed");
    }

    static void UpdateElement(string id, Action<ElementSettings> change)
    {
        // a copy of the current settings with one graphic changed, saved the normal way
        var s = JsonSerializer.Deserialize<ControlSettings>(JsonSerializer.Serialize(ControlState.Current, ControlState.Json), ControlState.Json)!;
        if (!s.Elements.TryGetValue(id, out var e)) { e = new ElementSettings(); s.Elements[id] = e; }
        change(e);
        ControlState.Replace(s);
    }

    void Dials(HttpListenerContext ctx, string path, string method)
    {
        if (path == "/api/dials" && method == "GET") { SendJson(ctx, DescribeDials()); return; }
        if (path == "/api/dials/upload" && method == "POST")
        {
            var bytes = ReadUpload(ctx.Request, 40L * 1024 * 1024);
            if (bytes == null) { Text(ctx, 413, "That picture is too big (40 MB max)."); return; }
            var ext = ImageExt(bytes);
            if (ext == null || !Decodes(bytes)) { Text(ctx, 400, "That file isn't a picture Grandstand can read (PNG, JPG or WebP)."); return; }
            string raw = ctx.Request.QueryString["name"] ?? "my face";
            string stem = new string(Path.GetFileNameWithoutExtension(raw).Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_').ToArray()).Trim();
            if (stem.Length == 0) stem = "my face"; if (stem.Length > 60) stem = stem[..60];
            Directory.CreateDirectory(ClockDial.DialsFolder);
            string name = stem + ext; int n = 2;
            while (File.Exists(Path.Combine(ClockDial.DialsFolder, name))) name = $"{stem} {n++}{ext}";
            File.WriteAllBytes(Path.Combine(ClockDial.DialsFolder, name), bytes);
            ClockDial.Select("file:" + name, null);
            SendJson(ctx, DescribeDials());
            return;
        }
        if (path == "/api/dial" && method == "POST")
        {
            string dial; bool? hands = null;
            try
            {
                using var doc = JsonDocument.Parse(ReadBody(ctx.Request));
                var root = doc.RootElement;
                dial = root.TryGetProperty("dial", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "auto" : "auto";
                if (root.TryGetProperty("subdialHands", out var h) && h.ValueKind is JsonValueKind.True or JsonValueKind.False) hands = h.GetBoolean();
            }
            catch { Text(ctx, 400, "bad dial request"); return; }
            if (dial.Length > 140) { Text(ctx, 400, "bad dial name"); return; }
            ClockDial.Select(dial, hands);
            SendJson(ctx, DescribeDials());
            return;
        }
        if (method == "GET" && path.StartsWith("/api/dials/thumb/", StringComparison.Ordinal))
        {
            string rest = path["/api/dials/thumb/".Length..];
            string? file = null;
            if (rest == "classic") file = ClockDial.HasClassic() ? Path.Combine(AssetsRoot, "clock", "dial.png") : null;
            else if (rest.StartsWith("builtin/", StringComparison.Ordinal)) file = ClockDial.BuiltInPath(rest["builtin/".Length..]);
            else if (rest.StartsWith("file/", StringComparison.Ordinal))
            {
                // only a plain file name from the dials folder (png/jpg/jpeg/webp): no folders, no ".."
                string name;
                try { name = Uri.UnescapeDataString(rest["file/".Length..]); } catch { name = ""; }
                file = ClockDial.CustomPath(name);
            }
            if (file == null || !File.Exists(file) || new FileInfo(file).Length > 40L * 1024 * 1024) { Text(ctx, 404, "no such dial"); return; }
            Send(ctx, 200, ContentType(file), File.ReadAllBytes(file));
            return;
        }
        Text(ctx, 404, "not found");
    }

    void Cards(HttpListenerContext ctx, string path, string method)
    {
        if (path == "/api/cards" && method == "GET") { SendJson(ctx, ListCards()); return; }
        if (path == "/api/cards/reload" && method == "POST") { var problems = CustomCards.LoadFolder(); SendJson(ctx, new { problems }); return; }
        string rest = path.Length > "/api/cards/".Length ? path["/api/cards/".Length..] : "";
        bool play = rest.EndsWith("/play", StringComparison.Ordinal);
        if (play) rest = rest[..^"/play".Length];
        string? id = SafeId(rest);
        if (id == null) { Text(ctx, 400, "card names use letters, numbers, - and _ (up to 40)"); return; }
        string file = Path.Combine(CustomCards.Folder, id + ".json");
        if (play && method == "POST") { CustomCards.Requests.Enqueue(id); SendJson(ctx, new { playing = id }); return; }
        if (method is "PUT" or "POST")
        {
            string body = ReadBody(ctx.Request);
            if (body.Length > 64 * 1024) { Text(ctx, 400, "card too big"); return; }
            try { CustomCards.Parse(body); }
            catch (Exception ex) { Text(ctx, 400, "bad card: " + ex.Message); return; }
            if (!File.Exists(file) && Directory.Exists(CustomCards.Folder) && Directory.GetFiles(CustomCards.Folder, "*.json").Length >= CustomCards.MaxCards)
            { Text(ctx, 400, $"at most {CustomCards.MaxCards} cards"); return; }
            Directory.CreateDirectory(CustomCards.Folder);
            File.WriteAllText(file, body);
            var problems = CustomCards.LoadFolder();
            SendJson(ctx, new { saved = id, problems });
            return;
        }
        if (method == "DELETE")
        {
            if (File.Exists(file)) File.Delete(file);
            var problems = CustomCards.LoadFolder();
            SendJson(ctx, new { deleted = id, problems });
            return;
        }
        if (method == "GET" && File.Exists(file)) { Send(ctx, 200, "application/json", File.ReadAllBytes(file)); return; }
        Text(ctx, 404, "no such card");
    }

    static object ListCards()
    {
        var cards = new List<object>();
        if (Directory.Exists(CustomCards.Folder))
            foreach (var f in Directory.GetFiles(CustomCards.Folder, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(CustomCards.MaxCards))
            {
                string id = Path.GetFileNameWithoutExtension(f);
                System.Text.Json.Nodes.JsonNode? json = null; string? error = null;
                try
                {
                    var text = File.ReadAllText(f);
                    CustomCards.Parse(text);
                    json = System.Text.Json.Nodes.JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                }
                catch (Exception ex) { error = ex.Message; }
                cards.Add(new { id, card = json, error });
            }
        return new { folder = CustomCards.Folder, cards, fields = CustomCards.BuiltInFields, loaded = CustomCards.Loaded.Count };
    }

    // ---- shareable packs: settings + sponsors + custom cards in one file ----
    static readonly string PacksFolder = Path.Combine(ControlState.Folder, "packs");

    static System.Text.Json.Nodes.JsonObject ExportPack(string name)
    {
        var cards = new System.Text.Json.Nodes.JsonObject();
        if (Directory.Exists(CustomCards.Folder))
            foreach (var f in Directory.GetFiles(CustomCards.Folder, "*.json").Take(CustomCards.MaxCards))
                try { cards[Path.GetFileNameWithoutExtension(f)] = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(f)); } catch { }
        var settings = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(ControlState.Current, ControlState.Json))!.AsObject();
        settings.Remove("director");   // a pack never switches the camera director on
        return new System.Text.Json.Nodes.JsonObject
        {
            ["grandstandPack"] = 1,
            ["name"] = name,
            ["saved"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            ["settings"] = settings,
            ["sponsors"] = SponsorPack.Export(),
            ["cards"] = cards,
        };
    }

    /// <summary>Applies a pack. Every part is validated before anything changes; bad cards are skipped.</summary>
    static List<string> ApplyPack(string json)
    {
        var problems = new List<string>();
        var root = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject
                   ?? throw new FormatException("not a Grandstand pack");
        if (root["grandstandPack"] is null) throw new FormatException("not a Grandstand pack (no grandstandPack field)");
        ControlSettings? settings = null;
        if (root["settings"] is System.Text.Json.Nodes.JsonObject so)
            settings = so.Deserialize<ControlSettings>(ControlState.Json) ?? throw new FormatException("bad settings in pack");
        string? sponsors = root["sponsors"] is System.Text.Json.Nodes.JsonObject sp ? sp.ToJsonString() : null;
        if (settings != null) { settings.Director = ControlState.Current.Director; ControlState.Replace(settings); }
        if (sponsors != null) { try { SponsorPack.Save(sponsors); } catch (Exception ex) { problems.Add("sponsors: " + ex.Message); } }
        if (root["cards"] is System.Text.Json.Nodes.JsonObject cards)
        {
            Directory.CreateDirectory(CustomCards.Folder);
            foreach (var (k, v) in cards)
            {
                var id = SafeId(k);
                if (id == null || v is null) { problems.Add("card '" + k + "': bad name"); continue; }
                string text = v.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                try { CustomCards.Parse(text); File.WriteAllText(Path.Combine(CustomCards.Folder, id + ".json"), text); }
                catch (Exception ex) { problems.Add("card '" + k + "': " + ex.Message); }
            }
            problems.AddRange(CustomCards.LoadFolder());
        }
        return problems;
    }

    void Packs(HttpListenerContext ctx, string path, string method)
    {
        if (path == "/api/packs" && method == "GET")
        {
            var names = Directory.Exists(PacksFolder)
                ? Directory.GetFiles(PacksFolder, "*.json").Select(Path.GetFileNameWithoutExtension).OrderBy(x => x).ToArray()
                : Array.Empty<string?>();
            SendJson(ctx, new { folder = PacksFolder, packs = names });
            return;
        }
        if (path == "/api/packs/current" && method == "GET") { Send(ctx, 200, "application/json", Encoding.UTF8.GetBytes(ExportPack("current").ToJsonString(new JsonSerializerOptions { WriteIndented = true }))); return; }
        if (path == "/api/packs/import" && method == "POST")
        {
            try { SendJson(ctx, new { applied = true, problems = ApplyPack(ReadBody(ctx.Request)), settings = ControlState.Current }); }
            catch (Exception ex) { Text(ctx, 400, ex.Message); }
            return;
        }
        string rest = path.Length > "/api/packs/".Length ? path["/api/packs/".Length..] : "";
        bool apply = rest.EndsWith("/apply", StringComparison.Ordinal);
        if (apply) rest = rest[..^"/apply".Length];
        string? id = SafeId(rest);
        if (id == null) { Text(ctx, 400, "pack names use letters, numbers, - and _ (up to 40)"); return; }
        string file = Path.Combine(PacksFolder, id + ".json");
        if (apply && method == "POST")
        {
            if (!File.Exists(file)) { Text(ctx, 404, "no such pack"); return; }
            try { SendJson(ctx, new { applied = id, problems = ApplyPack(File.ReadAllText(file)), settings = ControlState.Current }); }
            catch (Exception ex) { Text(ctx, 400, ex.Message); }
            return;
        }
        if (method is "PUT" or "POST")
        {
            Directory.CreateDirectory(PacksFolder);
            File.WriteAllText(file, ExportPack(id).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            SendJson(ctx, new { saved = id });
            return;
        }
        if (method == "DELETE") { if (File.Exists(file)) File.Delete(file); SendJson(ctx, new { deleted = id }); return; }
        if (method == "GET" && File.Exists(file)) { Send(ctx, 200, "application/json", File.ReadAllBytes(file)); return; }
        Text(ctx, 404, "no such pack");
    }

    // ---- viewer cards (2026-10-29, #494) ----
    static string? Str(JsonElement root, params string[] names)
    {
        foreach (var n in names)
            if (root.TryGetProperty(n, out var v))
                return v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.GetRawText(),
                    _ => null,
                };
        return null;
    }

    static object ViewerStatus() => new
    {
        cards = ApexHUD.Native.Overlays.ViewerFeed.Enabled,
        waiting = ApexHUD.Native.Overlays.ViewerFeed.Waiting,
        fund = new { enabled = ApexHUD.Native.Overlays.ViewerFund.Enabled, goal = ApexHUD.Native.Overlays.ViewerFund.Goal,
                     target = ApexHUD.Native.Overlays.ViewerFund.Target, current = ApexHUD.Native.Overlays.ViewerFund.Current,
                     file = ApexHUD.Native.Overlays.ViewerFund.FilePath },
        blocklist = ApexHUD.Native.Overlays.ViewerFeed.BlocklistPath,
        // #603: the tier looks and this stream's thank-you list
        tierLooks = ApexHUD.Native.Overlays.ViewerTiers.LooksOn,
        names = new { count = ApexHUD.Native.Overlays.ViewerRoll.Count, supporters = ApexHUD.Native.Overlays.ViewerRoll.Supporters,
                      started = ApexHUD.Native.Overlays.ViewerRoll.Started.ToString("yyyy-MM-dd HH:mm"), file = ApexHUD.Native.Overlays.ViewerRoll.FilePath },
        rollPlaying = ApexHUD.Native.Overlays.ViewerTierRenderer.RollActive,
    };

    static int? Int(JsonElement root, params string[] names)
    {
        foreach (var n in names)
            if (root.TryGetProperty(n, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d)) return (int)Math.Clamp(d, int.MinValue, int.MaxValue);
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int i)) return i;
            }
        return null;
    }

    static bool Bool(JsonElement root, params string[] names)
    {
        foreach (var n in names)
            if (root.TryGetProperty(n, out var v))
                return v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase));
        return false;
    }

    // 2026-11-05: the Integrations page (Garage 61, Discord, league standings, Stream Deck, status of everything)
    void IntegrationsApi(HttpListenerContext ctx, string path, string method)
    {
        var req = ctx.Request;
        if (path == "/api/integrations" && method == "GET")
        {
            SendJson(ctx, new { settings = Integrations.Current, status = Integrations.Status(raceState), fields = Integrations.DataFields.Select(f => new { name = f.Name, desc = f.Desc }) });
            return;
        }
        if (path == "/api/integrations" && method == "POST")
        {
            var s = JsonSerializer.Deserialize<Integrations.Settings>(ReadBody(req), ControlState.Json);
            if (s == null) { Text(ctx, 400, "bad settings"); return; }
            s.DiscordWebhook = (s.DiscordWebhook ?? "").Trim(); s.LeagueSource = (s.LeagueSource ?? "").Trim();
            s.LeagueName = (s.LeagueName ?? "").Trim(); s.DiscordStreamLink = (s.DiscordStreamLink ?? "").Trim();
            Integrations.Save(s);
            SendJson(ctx, new { ok = true });
            return;
        }
        if (path == "/api/integrations/g61/key" && method == "POST")
        {
            string token = "";
            try { using var doc = JsonDocument.Parse(ReadBody(req)); token = doc.RootElement.GetProperty("token").GetString() ?? ""; } catch { }
            if (token.Length > 400) { Text(ctx, 400, "too long"); return; }
            Integrations.SaveG61Token(token);
            SendJson(ctx, Integrations.TestG61().GetAwaiter().GetResult());
            return;
        }
        if (path == "/api/integrations/g61/test" && method == "POST") { SendJson(ctx, Integrations.TestG61().GetAwaiter().GetResult()); return; }
        if (path == "/api/integrations/discord/test" && method == "POST") { SendJson(ctx, new { result = Integrations.TestDiscord().GetAwaiter().GetResult() }); return; }
        // 2026-11-06: YouTube (the Google client file, sign-in / out), Hue (find, pair, rooms, test)
        if (path == "/api/integrations/youtube/client" && method == "POST")
        {
            string body = ReadBody(req);
            if (body.Length > 64 * 1024) { Text(ctx, 400, "too big"); return; }
            SendJson(ctx, new { problem = YouTubeUpload.SaveClient(body), status = YouTubeUpload.Status });
            return;
        }
        if (path == "/api/integrations/youtube/signin" && method == "POST") { YouTubeUpload.BeginSignIn(); SendJson(ctx, new { status = YouTubeUpload.Status }); return; }
        if (path == "/api/integrations/youtube/signout" && method == "POST") { YouTubeUpload.SignOut(); SendJson(ctx, new { status = YouTubeUpload.Status }); return; }
        if (path == "/api/integrations/hue/discover" && method == "POST") { SendJson(ctx, HueLights.Discover().GetAwaiter().GetResult()); return; }
        if (path == "/api/integrations/hue/pair" && method == "POST") { SendJson(ctx, HueLights.Pair().GetAwaiter().GetResult()); return; }
        if (path == "/api/integrations/hue/rooms" && method == "GET") { SendJson(ctx, HueLights.Rooms().GetAwaiter().GetResult()); return; }
        if (path == "/api/integrations/hue/test" && method == "POST") { HueLights.Test(); SendJson(ctx, new { ok = true }); return; }
        // 2026-11-06: viewer bets
        if (path == "/api/integrations/bets/test" && method == "POST") { Bets.Test(); SendJson(ctx, new { ok = true }); return; }
        if (path == "/api/integrations/bets/twitchtest" && method == "POST") { SendJson(ctx, new { result = Bets.TestTwitch() }); return; }
        if (path == "/api/integrations/bets/reset" && method == "POST") { Bets.ResetPoints(); SendJson(ctx, new { ok = true }); return; }
        if (path == "/api/integrations/league/reload" && method == "POST") { Integrations.ReloadLeague().GetAwaiter().GetResult(); SendJson(ctx, new { status = Integrations.LeagueStatus, rows = Integrations.League.Take(10) }); return; }
        Text(ctx, 404, "not found");
    }

    /// <summary>2026-11-06: what the session bar shows, for the control app's top bar (live race, or the sample race).</summary>
    object BarInfo()
    {
        var s = compositor.CurrentSnapshot;
        bool live = raceState.Current.IsConnected;
        bool f = (ControlState.Current.TempUnit == "auto" ? s.TemperatureUnit : ControlState.Current.TempUnit).StartsWith("F", StringComparison.OrdinalIgnoreCase);
        string T(float c) => (f ? Math.Round(c * 9 / 5 + 32) : Math.Round(c)).ToString(System.Globalization.CultureInfo.InvariantCulture) + (f ? "°F" : "°C");
        var tod = TimeSpan.FromSeconds(Math.Max(0, s.SessionTimeOfDay) % 86400);
        string label = s.Phase switch
        {
            RacePhase.PreRace => s.SessionState == 3 ? "FORMATION LAP" : "PRE-RACE",
            RacePhase.PostRace => "FINISHED",
            _ => (s.SessionTypeName ?? "").ToUpperInvariant(),
        };
        double len = s.SessionLengthSeconds, left = s.SessionTimeRemaining;
        var leader = s.ClassLeaderboard.FirstOrDefault()?.Drivers.FirstOrDefault(d => d.PositionInClass == 1);
        bool any = s.IsConnected && !string.IsNullOrEmpty(s.TrackDisplayName);
        return new
        {
            live, sample = OverlayCompositor.SampleRunning, any,
            track = any ? (s.TrackBroadcastName.Length > 0 ? s.TrackBroadcastName : s.TrackDisplayName).ToUpperInvariant() : "",
            country = (s.TrackCountryCode ?? "").ToLowerInvariant(),
            air = any ? T(s.AirTempC) : "", trackTemp = any ? T(s.TrackTempC) : "", local = any ? $"{tod.Hours:00}:{tod.Minutes:00}" : "",
            label = any ? label : "", timer = any && left > 0 ? LapTimeFormat.SessionTimer(left) : "",
            lap = any ? s.CurrentLap : 0, laps = s.TotalLaps is > 0 and < 2000 ? s.TotalLaps : 0,
            progress = any && len > 0 && left >= 0 ? Math.Clamp(1 - left / len, 0, 1) : 0,
            leaderClass = leader != null ? (s.ClassLeaderboard.FirstOrDefault()?.Name ?? "").ToUpperInvariant() : "",
            leader = leader != null ? $"#{leader.CarNumber} {ApexHUD.Native.Common.NameFormat.Broadcast(leader)}" : "",
        };
    }

    // 2026-11-06: the producer desk (ProducerDesk.cs)
    void DeskApi(HttpListenerContext ctx, string path, string method)
    {
        var q = ctx.Request.QueryString;
        int I(string k, int d = -1) => int.TryParse(q[k], out int v) ? v : d;
        if (path == "/api/desk" && method == "GET") { SendJson(ctx, ProducerDesk.Describe()); return; }
        if (path == "/api/desk" && method == "POST")
        {
            var s = JsonSerializer.Deserialize<ProducerDesk.Settings>(ReadBody(ctx.Request), ControlState.Json);
            if (s == null) { Text(ctx, 400, "bad settings"); return; }
            s.Commentators = (s.Commentators ?? new()).Where(c => (c.Name ?? "").Trim().Length > 0).Take(12)
                .Select(c => new ProducerDesk.Commentator { Name = c.Name.Trim()[..Math.Min(60, c.Name.Trim().Length)], Role = (c.Role ?? "").Trim()[..Math.Min(40, (c.Role ?? "").Trim().Length)] }).ToList();
            ProducerDesk.Save(s);
            SendJson(ctx, new { ok = true });
            return;
        }
        if (method != "POST") { Text(ctx, 404, "not found"); return; }
        string msg = path switch
        {
            "/api/desk/mark" => ProducerDesk.Mark(q["label"] ?? ""),
            "/api/desk/replay" => ProducerDesk.PlayMoment(I("i"), q["slow"] == "1"),
            "/api/desk/live" => ProducerDesk.BackToLive(),
            "/api/desk/show" => ProducerDesk.Show(q["what"] ?? "", I("a"), I("b"), I("i")),
            "/api/desk/penalty" => ProducerDesk.Penalty(I("car"), q["kind"] ?? "penalty", q["text"] ?? ""),
            "/api/desk/roster" => Reload(),
            _ => "",
        };
        if (msg.Length == 0) { Text(ctx, 404, "not found"); return; }
        SendJson(ctx, new { result = msg });
        static string Reload() { ProducerDesk.ReloadRoster().GetAwaiter().GetResult(); return ProducerDesk.RosterStatus; }
    }

    /// <summary>Stream Deck keys (Grandstand.exe --do ...): ON AIR, and switching one graphic on or off.</summary>
    void DoApi(HttpListenerContext ctx, string path)
    {
        var cur = ControlState.Current;
        var s = JsonSerializer.Deserialize<ControlSettings>(JsonSerializer.Serialize(cur, ControlState.Json), ControlState.Json)!;
        if (path == "/api/do/onair")
        {
            string to = ctx.Request.QueryString["to"] ?? "";
            s.OnAir = to == "1" ? true : to == "0" ? false : !cur.OnAir;
            ControlState.Replace(s);
            SendJson(ctx, new { onAir = s.OnAir });
            return;
        }
        if (path == "/api/do/toggle")
        {
            string id = ctx.Request.QueryString["id"] ?? "";
            if (id.Length is 0 or > 40) { Text(ctx, 400, "which graphic?"); return; }
            if (!s.Elements.TryGetValue(id, out var el)) s.Elements[id] = el = new ElementSettings();
            el.Enabled = !el.Enabled;
            ControlState.Replace(s);
            SendJson(ctx, new { id, enabled = el.Enabled });
            return;
        }
        Text(ctx, 404, "not found");
    }

    void Viewer(HttpListenerContext ctx, string path, string method)
    {
        if (path == "/api/viewer" && method == "GET") { SendJson(ctx, ViewerStatus()); return; }
        JsonElement root;
        string body = ReadBody(ctx.Request);
        if (body.Length > 16 * 1024) { Text(ctx, 400, "too big"); return; }
        try { using var doc = JsonDocument.Parse(body.Length == 0 ? "{}" : body); root = doc.RootElement.Clone(); }
        catch { Text(ctx, 400, "send JSON: {\"type\":\"superchat\",\"name\":\"...\",\"amount\":\"$5.00\",\"message\":\"...\",\"platform\":\"youtube\"}"); return; }
        if (root.ValueKind != JsonValueKind.Object) { Text(ctx, 400, "send a JSON object"); return; }
        if (path == "/api/viewer" && method == "POST")
        {
            // #603: the bridge's extra fields (all optional): currency, gifts, months, id, recipient, partOfGift
            var r = ApexHUD.Native.Overlays.ViewerFeed.Accept(new ApexHUD.Native.Overlays.ViewerInput(Str(root, "type", "kind"), Str(root, "name", "user", "userName"),
                Str(root, "amount"), Str(root, "message", "text"), Str(root, "platform", "source"),
                Currency: Str(root, "currency", "currencyCode"), Gifts: Int(root, "gifts", "count"), Months: Int(root, "months"),
                Id: Str(root, "id", "messageId", "msgId"), Recipient: Str(root, "recipient", "recipientUser"), PartOfGift: Bool(root, "partOfGift", "fromGiftBomb")));
            if (!r.Accepted) { Text(ctx, 400, r.Why); return; }
            SendJson(ctx, new { accepted = r.Accepted, onScreen = r.OnScreen, why = r.Why });
            return;
        }
        // the control app's Test buttons: a sample event (never counts towards the track fund)
        if (path == "/api/viewer/test" && method == "POST")
        {
            var r = ApexHUD.Native.Overlays.ViewerFeed.Test(Str(root, "type") ?? ctx.Request.QueryString["type"]);
            if (!r.Accepted) { Text(ctx, 400, r.Why); return; }
            SendJson(ctx, new { accepted = r.Accepted, onScreen = r.OnScreen, why = r.Why + (r.OnScreen ? " - playing now (a test skips the wait for a clear screen)" : "") });
            return;
        }
        // #603: the thank-you roll - {"action": "play"} (this stream's names), "sample" (made-up names, for a look), "stop"
        if (path == "/api/viewer/thanks" && method == "POST")
        {
            string act = (Str(root, "action") ?? "play").Trim().ToLowerInvariant();
            if (act == "stop") ApexHUD.Native.Overlays.ViewerTierRenderer.StopRoll();
            else if (act is "play" or "sample") ApexHUD.Native.Overlays.ViewerTierRenderer.RequestRoll(act == "sample");
            else { Text(ctx, 400, "action: play, sample or stop"); return; }
            SendJson(ctx, ViewerStatus());
            return;
        }
        // #603: a new stream - forget the names gathered so far (control app "New stream")
        if (path == "/api/viewer/names/clear" && method == "POST")
        {
            ApexHUD.Native.Overlays.ViewerRoll.Clear();
            ApexHUD.Native.Overlays.ViewerFeed.ForgetSeen();
            ApexHUD.Native.Overlays.ViewerLatest.Clear();   // #608: the latest plate starts empty too
            SendJson(ctx, ViewerStatus());
            return;
        }
        // set / reset the track fund's current amount: {"current": 0}
        if (path == "/api/viewer/fund" && method == "POST")
        {
            if (!root.TryGetProperty("current", out var cv) || !cv.TryGetDouble(out double v) || !double.IsFinite(v) || v < 0)
            { Text(ctx, 400, "send {\"current\": amount}"); return; }
            ApexHUD.Native.Overlays.ViewerFund.Set(v);
            SendJson(ctx, ViewerStatus());
            return;
        }
        Text(ctx, 404, "not found");
    }

    static string ReadBody(HttpListenerRequest req)
    {
        if (req.ContentLength64 > 2_000_000) throw new InvalidOperationException("body too large");
        using var r = new StreamReader(req.InputStream, Encoding.UTF8);
        var buf = new char[2_000_001];
        int n = 0, k;
        while (n < buf.Length && (k = r.Read(buf, n, buf.Length - n)) > 0) n += k;   // chunked uploads are capped too
        if (n > 2_000_000) throw new InvalidOperationException("body too large");
        return new string(buf, 0, n);
    }

    static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".json" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".ttf" => "font/ttf",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };

    static void SendJson(HttpListenerContext ctx, object o) =>
        Send(ctx, 200, "application/json", JsonSerializer.SerializeToUtf8Bytes(o, ControlState.Json));

    static void Send(HttpListenerContext ctx, int status, string type, byte[] body)
    {
        var res = ctx.Response;
        res.StatusCode = status;
        res.ContentType = type;
        res.Headers["Cache-Control"] = "no-store";
        res.ContentLength64 = body.Length;
        res.OutputStream.Write(body, 0, body.Length);
        res.OutputStream.Close();
    }

    public void Dispose()
    {
        running = false;
        try { listener.Stop(); listener.Close(); } catch { }
    }
}
