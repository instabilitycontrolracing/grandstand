using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ApexHUD.Native.Overlays;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// Highlight markers -> OBS (2026-10-28, #487). Off unless the control app's "Highlights to OBS" is on.
///
/// Talks to OBS on this PC through OBS's own remote-control link (obs-websocket v5, built into OBS 28 and later),
/// with the port and password read from OBS's own settings file
/// (%APPDATA%\obs-studio\plugin_config\obs-websocket\config.json) - nothing to type, nothing stored by Grandstand,
/// the password is never logged. For each marker:
///   - CreateRecordChapter "L12 Up to P3 in GTP" while OBS records (OBS 30.2+, Hybrid MP4: chapters show in editors/players)
///   - if OBS's Replay Buffer is running: SaveReplayBuffer 6 s after the moment, so the clip holds the lead-up and the
///     aftermath (clip length = the buffer length set in OBS).
/// If OBS isn't running, the link is off in OBS, or a request isn't supported, it quietly does nothing (the markers
/// are still in the highlight files). One background task; the render thread only drops markers into a queue.
/// </summary>
internal static class ObsMarkers
{
    static readonly Channel<object> queue = Channel.CreateBounded<object>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });

    // ---- 2026-10-28 (#488): the streaming director - Grandstand picks the OBS scene from what's on screen.
    // Only these scenes are ever switched to, and only while OBS is already showing one of them (pick any other scene,
    // e.g. "Grandstand Broadcast", and the director leaves OBS alone until you pick one of these again).
    public static readonly string[] DirectorScenes = { "Racing", "Battle", "Story card", "Onboard", "Replay", "Post-race", "Starting soon" };
    sealed record SceneReq(string Name);
    sealed record StatusReq();
    /// <summary>2026-10-28 (#501): what OBS is doing (for the stream deck's side panel via /api/obs). Refreshed every 2 s
    /// while the OBS link is wanted; Linked=false when OBS isn't reachable.</summary>
    public sealed record ObsStatus(bool Linked, string Scene, bool Streaming, bool Recording, bool Buffer, DateTime At);
    public static volatile ObsStatus Status = new(false, "", false, false, false, DateTime.MinValue);
    static string wantName = "", sentName = "";
    static long wantSinceMs, sentAtMs;

    /// <summary>Render thread, every tick: the scene that fits what's on screen now. Holds 1.2 s before switching (no
    /// flicker), at most one switch every 3 s, except straight into Replay / Onboard / Post-race (those cut at once).</summary>
    public static void WantScene(string name, long nowMs)
    {
        if (!ControlState.Current.ObsDirector) { wantName = sentName = ""; return; }
        if (name != wantName) { wantName = name; wantSinceMs = nowMs; }
        if (name == sentName) return;
        bool cut = name is "Replay" or "Onboard" or "Post-race";
        if (!cut && (nowMs - wantSinceMs < 1200 || nowMs - sentAtMs < 3000)) return;
        sentName = name; sentAtMs = nowMs;
        queue.Writer.TryWrite(new SceneReq(name));
    }
    static int started;

    // ---- 2026-10-29 (#9): the highlight reel records itself. These are sent whatever the "Highlights to OBS" switch
    // says (the reel was started by hand), and answer back so the reel knows what OBS did.
    /// <summary>What a reel request got back: <paramref name="Ok"/> OBS did it; <paramref name="WasRecording"/> OBS was
    /// already recording before StartRecord (the reel then leaves the recording alone); <paramref name="OutputPath"/> the
    /// saved file after StopRecord; Ok=false with <paramref name="Problem"/> when OBS isn't reachable or said no.</summary>
    public sealed record ReelResult(bool Ok, bool WasRecording, string OutputPath, string Problem);
    sealed record ReelReq(string Kind, string Chapter, TaskCompletionSource<ReelResult> Done);

    /// <summary>Starts an OBS recording (unless one is already running). Never throws; times out after 8 s.</summary>
    public static Task<ReelResult> StartRecordAsync() => Reel("start", "");
    /// <summary>Stops the OBS recording; the result carries the saved file's path.</summary>
    public static Task<ReelResult> StopRecordAsync() => Reel("stop", "");
    /// <summary>Pauses our recording while the replay jumps (no frozen frames in the video).</summary>
    public static Task<ReelResult> PauseRecordAsync() => Reel("pause", "");
    public static Task<ReelResult> ResumeRecordAsync() => Reel("resume", "");
    /// <summary>A chapter in the running recording (OBS 30.2+, Hybrid MP4); quietly nothing on older OBS.</summary>
    public static Task<ReelResult> ChapterAsync(string name) => Reel("chapter", name);
    /// <summary>2026-11-06 (vertical clips): makes sure OBS's Replay Buffer is switched on in its settings, holds at least
    /// <paramref name="seconds"/>, and is running. Ok + WasRecording=true when it was already running (it has the past).</summary>
    public static Task<ReelResult> EnsureReplayBufferAsync(int seconds) => Reel("rbensure", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
    /// <summary>2026-11-06 (vertical clips): saves the Replay Buffer; OutputPath = the saved file.</summary>
    public static Task<ReelResult> SaveReplayAsync() => Reel("rbsave", "");
    /// <summary>Asked for by the vertical clips (keeps the 2 s status poll going while they want the buffer).</summary>
    public static volatile bool WantStatus;

    static async Task<ReelResult> Reel(string kind, string chapter)
    {
        Start();
        var tcs = new TaskCompletionSource<ReelResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.Writer.TryWrite(new ReelReq(kind, chapter, tcs))) return new ReelResult(false, false, "", "OBS queue closed");
        var done = await Task.WhenAny(tcs.Task, Task.Delay(kind == "rbsave" ? 15000 : 8000));   // saving waits for OBS to write the file
        return done == tcs.Task ? tcs.Task.Result : new ReelResult(false, false, "", "OBS didn't answer in time");
    }
    static void FailReel(object item, string why) { if (item is ReelReq r) r.Done.TrySetResult(new ReelResult(false, false, "", why)); }

    public static void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 1) return;
        HighlightLog.Marked += m => { if (ControlState.Current.HighlightsObs) queue.Writer.TryWrite(m); };   // records are objects
        Task.Run(Loop);
        Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(2000);
                var c = ControlState.Current;
                if (c.ObsDirector || c.HighlightsObs || WantStatus) queue.Writer.TryWrite(new StatusReq());
            }
        });
    }

    internal sealed record ObsConfig(int Port, string? Password);

    /// <summary>Port and password from OBS's own obs-websocket settings; null when the link is off / not set up.</summary>
    internal static ObsConfig? ReadConfig(string? path = null)
    {
        try
        {
            // 2026-10-28: Grandstand runs elevated (admin task) - Roaming may not be the user's; try the user's own folders too
            const string rel = @"obs-studio\plugin_config\obs-websocket\config.json";
            var tries = new List<string>();
            if (path != null) tries.Add(path);
            tries.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), rel));
            tries.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming", rel));
            try { tries.Add(Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)) ?? "", "AppData", "Roaming", rel)); } catch { }
            try { foreach (var u in Directory.GetDirectories(@"C:\Users")) tries.Add(Path.Combine(u, "AppData", "Roaming", rel)); } catch { }
            path = tries.FirstOrDefault(File.Exists);
            if (path == null) { LastConfigProblem = "no OBS websocket settings file (looked in " + tries[0] + ")"; return null; }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            if (r.TryGetProperty("server_enabled", out var en) && en.ValueKind == JsonValueKind.False) { LastConfigProblem = "remote control is switched off in OBS"; return null; }
            int port = r.TryGetProperty("server_port", out var p) && p.TryGetInt32(out var pp) ? pp : 4455;
            bool auth = !r.TryGetProperty("auth_required", out var a) || a.ValueKind != JsonValueKind.False;
            string? pw = auth && r.TryGetProperty("server_password", out var s) ? s.GetString() : null;
            return new ObsConfig(port, pw);
        }
        catch (Exception ex) { LastConfigProblem = ex.GetType().Name + ": " + ex.Message; return null; }
    }
    internal static string LastConfigProblem = "";

    /// <summary>obs-websocket v5 authentication string: base64(sha256(base64(sha256(password + salt)) + challenge)).</summary>
    internal static string AuthString(string password, string salt, string challenge)
    {
        string secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    /// <summary>The chapter name for a marker (OBS shows it in the recording's chapter list).</summary>
    internal static string ChapterName(HighlightLog.Mark m) => (m.Lap > 0 ? $"L{m.Lap} " : "") + m.Label;

    static async Task Loop()
    {
        ClientWebSocket? ws = null;
        var buf = new byte[64 * 1024];
        int reqId = 0;
        DateTime nextTry = DateTime.MinValue;
        bool toldOff = false;
        await foreach (var item in queue.Reader.ReadAllAsync())
        {
            try
            {
                if (ws is not { State: WebSocketState.Open })
                {
                    if (DateTime.UtcNow < nextTry && item is not ReelReq) { if (item is SceneReq) sentName = ""; continue; }   // retry the scene later (the reel always tries now)
                    ws?.Dispose(); ws = null;
                    var cfg = ReadConfig();
                    if (cfg == null) { nextTry = DateTime.UtcNow.AddSeconds(20); FailReel(item, LastConfigProblem); if (item is SceneReq) sentName = ""; if (!toldOff) Program.Log("obs: can't use OBS's remote control (" + LastConfigProblem + ") - markers stay in the highlight files"); toldOff = true; continue; }
                    toldOff = false;
                    ws = await Connect(cfg, buf);
                    if (ws == null) { nextTry = DateTime.UtcNow.AddSeconds(10); FailReel(item, "OBS isn't running or its remote control is off"); if (item is SceneReq) sentName = ""; Status = Status with { Linked = false, At = DateTime.UtcNow }; continue; }
                }
                if (item is SceneReq sr)
                {
                    var cur = await Request(ws, buf, "GetCurrentProgramScene", null, ++reqId);
                    string now = cur is { } c && c.TryGetProperty("sceneName", out var sn) ? sn.GetString() ?? "" : "";
                    if (now != sr.Name && Array.IndexOf(DirectorScenes, now) >= 0)
                    {
                        await Request(ws, buf, "SetCurrentProgramScene", new { sceneName = sr.Name }, ++reqId);
                        Program.Log($"obs director: {now} -> {sr.Name}");
                    }
                    continue;
                }
                if (item is StatusReq)
                {
                    var sc0 = await Request(ws, buf, "GetCurrentProgramScene", null, ++reqId);
                    var st0 = await Request(ws, buf, "GetStreamStatus", null, ++reqId);
                    var rc0 = await Request(ws, buf, "GetRecordStatus", null, ++reqId);
                    var rb0 = await Request(ws, buf, "GetReplayBufferStatus", null, ++reqId);
                    static bool Act(JsonElement? e) => e is { } x && x.TryGetProperty("outputActive", out var a) && a.ValueKind == JsonValueKind.True;
                    string scene = sc0 is { } y && y.TryGetProperty("sceneName", out var n) ? n.GetString() ?? "" : "";
                    Status = new ObsStatus(true, scene, Act(st0), Act(rc0), Act(rb0), DateTime.UtcNow);
                    continue;
                }
                if (item is ReelReq rr)
                {
                    if (rr.Kind == "start")
                    {
                        var rs0 = await Request(ws, buf, "GetRecordStatus", null, ++reqId);
                        bool already = rs0 is { } q && q.TryGetProperty("outputActive", out var oa) && oa.ValueKind == JsonValueKind.True;
                        if (already) { rr.Done.TrySetResult(new ReelResult(true, true, "", "")); continue; }
                        bool on = (await RequestFull(ws, buf, "StartRecord", null, ++reqId)).Ok;
                        rr.Done.TrySetResult(new ReelResult(on, false, "", on ? "" : "OBS didn't start recording"));
                        Program.Log(on ? "obs: recording started for the highlight reel" : "obs: OBS didn't start recording for the highlight reel");
                    }
                    else if (rr.Kind == "stop")
                    {
                        var r = await Request(ws, buf, "StopRecord", null, ++reqId);
                        string outPath = r is { } q && q.TryGetProperty("outputPath", out var op) ? op.GetString() ?? "" : "";
                        rr.Done.TrySetResult(new ReelResult(r != null, false, outPath, r != null ? "" : "OBS didn't stop the recording"));
                        Program.Log("obs: recording stopped" + (outPath.Length > 0 ? " - " + outPath : ""));
                    }
                    else if (rr.Kind == "rbensure")
                    {
                        var st1 = await Request(ws, buf, "GetReplayBufferStatus", null, ++reqId);
                        bool running = st1 is { } q1 && q1.TryGetProperty("outputActive", out var a1) && a1.ValueKind == JsonValueKind.True;
                        if (running) { rr.Done.TrySetResult(new ReelResult(true, true, "", "")); continue; }
                        // not running: switch the buffer on in OBS's output settings with enough seconds, then start it
                        int want = int.TryParse(rr.Chapter, out var w0) ? w0 : 45;
                        var mode = await Request(ws, buf, "GetProfileParameter", new { parameterCategory = "Output", parameterName = "Mode" }, ++reqId);
                        string m0 = mode is { } mm && mm.TryGetProperty("parameterValue", out var mv) ? mv.GetString() ?? "Simple" : "Simple";
                        string cat = m0.Equals("Advanced", StringComparison.OrdinalIgnoreCase) ? "AdvOut" : "SimpleOutput";
                        await Request(ws, buf, "SetProfileParameter", new { parameterCategory = cat, parameterName = "RecRB", parameterValue = "true" }, ++reqId);
                        var len = await Request(ws, buf, "GetProfileParameter", new { parameterCategory = cat, parameterName = "RecRBTime" }, ++reqId);
                        int have = len is { } ll && ll.TryGetProperty("parameterValue", out var lv) && int.TryParse(lv.GetString(), out var hv) ? hv : 0;
                        if (have < want)
                        {
                            await Request(ws, buf, "SetProfileParameter", new { parameterCategory = cat, parameterName = "RecRBTime", parameterValue = want.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ++reqId);
                            Program.Log($"obs: replay buffer length {have} -> {want} s ({cat})");
                        }
                        bool on = (await RequestFull(ws, buf, "StartReplayBuffer", null, ++reqId)).Ok;
                        rr.Done.TrySetResult(new ReelResult(on, false, "", on ? "" : "OBS wouldn't start its Replay Buffer (check Settings > Output > Replay Buffer in OBS)"));
                        Program.Log(on ? "obs: replay buffer started (for vertical clips)" : "obs: OBS wouldn't start the replay buffer");
                    }
                    else if (rr.Kind == "rbsave")
                    {
                        var before = await Request(ws, buf, "GetLastReplayBufferReplay", null, ++reqId);
                        string prev = before is { } b0 && b0.TryGetProperty("savedReplayPath", out var p0) ? p0.GetString() ?? "" : "";
                        bool ok = (await RequestFull(ws, buf, "SaveReplayBuffer", null, ++reqId)).Ok;
                        if (!ok) { rr.Done.TrySetResult(new ReelResult(false, false, "", "OBS's Replay Buffer isn't running")); continue; }
                        string got = "";
                        for (int i = 0; i < 24 && got.Length == 0; i++)   // OBS writes the file, then reports it (up to ~6 s)
                        {
                            await Task.Delay(250);
                            var after = await Request(ws, buf, "GetLastReplayBufferReplay", null, ++reqId);
                            string p1 = after is { } b1 && b1.TryGetProperty("savedReplayPath", out var pp) ? pp.GetString() ?? "" : "";
                            if (p1.Length > 0 && p1 != prev) got = p1;
                        }
                        rr.Done.TrySetResult(new ReelResult(got.Length > 0, false, got, got.Length > 0 ? "" : "OBS saved the replay but didn't say where"));
                        Program.Log("obs: replay saved" + (got.Length > 0 ? " - " + got : " (no path)"));
                    }
                    else if (rr.Kind is "pause" or "resume")
                    {
                        bool ok = (await RequestFull(ws, buf, rr.Kind == "pause" ? "PauseRecord" : "ResumeRecord", null, ++reqId)).Ok;
                        rr.Done.TrySetResult(new ReelResult(ok, false, "", ok ? "" : "OBS said no (pausing isn't possible with some recording settings)"));
                    }
                    else
                    {
                        await Request(ws, buf, "CreateRecordChapter", new { chapterName = rr.Chapter }, ++reqId);
                        rr.Done.TrySetResult(new ReelResult(true, false, "", ""));
                    }
                    continue;
                }
                if (item is SaveClip sc)
                {
                    await Request(ws, buf, "SaveReplayBuffer", null, ++reqId);
                    Program.Log($"obs markers: clip saved - {sc.Label}");
                    continue;
                }
                if (item is not HighlightLog.Mark m) continue;
                await Request(ws, buf, "CreateRecordChapter", new { chapterName = ChapterName(m) }, ++reqId);
                var st = await Request(ws, buf, "GetReplayBufferStatus", null, ++reqId);
                if (st is { } j && j.TryGetProperty("outputActive", out var act) && act.ValueKind == JsonValueKind.True)
                {
                    var mark = m;
                    _ = Task.Run(async () => { await Task.Delay(6000); queue.Writer.TryWrite(new SaveClip(mark.Label)); });   // don't hold up scene changes
                }
            }
            catch (Exception ex)
            {
                Program.Log("obs: " + ex.GetType().Name);
                try { ws?.Dispose(); } catch { }
                ws = null; nextTry = DateTime.UtcNow.AddSeconds(30);
                if (item is SceneReq) sentName = "";
                FailReel(item, "OBS link dropped (" + ex.GetType().Name + ")");
            }
        }
    }
    sealed record SaveClip(string Label);

    static async Task<ClientWebSocket?> Connect(ObsConfig cfg, byte[] buf)
    {
        var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try { await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{cfg.Port}"), cts.Token); }
        catch { ws.Dispose(); return null; }       // OBS not running
        using var hello = await Receive(ws, buf);
        var d = hello.RootElement.GetProperty("d");
        string? auth = null;
        if (d.TryGetProperty("authentication", out var a))
        {
            if (string.IsNullOrEmpty(cfg.Password)) { ws.Dispose(); Program.Log("obs markers: OBS wants a password but none is set in its settings"); return null; }
            auth = AuthString(cfg.Password, a.GetProperty("salt").GetString() ?? "", a.GetProperty("challenge").GetString() ?? "");
        }
        await Send(ws, new { op = 1, d = auth == null ? (object)new { rpcVersion = 1, eventSubscriptions = 0 } : new { rpcVersion = 1, authentication = auth, eventSubscriptions = 0 } });
        using var ident = await Receive(ws, buf);
        if (ident.RootElement.GetProperty("op").GetInt32() != 2) { ws.Dispose(); Program.Log("obs markers: OBS refused the link"); return null; }
        Program.Log("obs markers: linked to OBS");
        return ws;
    }

    static async Task<JsonElement?> Request(ClientWebSocket ws, byte[] buf, string type, object? data, int id)
        => (await RequestFull(ws, buf, type, data, id)).Data;

    /// <summary>A request with its outcome: Ok = OBS did it (many requests answer with no data, so Data alone can't tell).</summary>
    static async Task<(bool Ok, JsonElement? Data)> RequestFull(ClientWebSocket ws, byte[] buf, string type, object? data, int id)
    {
        string rid = "gs" + id;
        await Send(ws, new { op = 6, d = data == null ? (object)new { requestType = type, requestId = rid } : new { requestType = type, requestId = rid, requestData = data } });
        for (int i = 0; i < 20; i++)
        {
            using var doc = await Receive(ws, buf);
            var r = doc.RootElement;
            if (r.GetProperty("op").GetInt32() != 7) continue;
            var d = r.GetProperty("d");
            if (d.GetProperty("requestId").GetString() != rid) continue;
            bool ok = d.GetProperty("requestStatus").GetProperty("result").GetBoolean();
            if (!ok) return (false, null);                         // e.g. not recording / chapters unsupported: fine
            return (true, d.TryGetProperty("responseData", out var rd) ? rd.Clone() : default(JsonElement?));
        }
        return (false, null);
    }

    static Task Send(ClientWebSocket ws, object o)
        => ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(o), WebSocketMessageType.Text, true, CancellationToken.None);

    static async Task<JsonDocument> Receive(ClientWebSocket ws, byte[] buf)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do { r = await ws.ReceiveAsync(buf, cts.Token); ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
        ms.Position = 0;
        return JsonDocument.Parse(ms);
    }
}
