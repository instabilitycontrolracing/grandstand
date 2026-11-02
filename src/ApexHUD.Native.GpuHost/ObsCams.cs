using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using ApexHUD.Native.Overlays;
using SkiaSharp;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// #611 (2026-10-30): the light cam placer's OBS side. Keeps the face cam and the pedal cam in OBS's program scene clear
/// of Grandstand's graphics (the "Grandstand overlay" source sits above the cams in every scene, so a graphic covers them).
/// <list type="bullet">
/// <item>The render thread drops the graphics' rects (1080 units) into <see cref="Publish"/> every ~150 ms - visible ones
/// and ones about to appear; it never waits on OBS.</item>
/// <item>One background task owns its own obs-websocket link (OBS's built-in remote control, settings read from OBS's own
/// file as <see cref="ObsMarkers"/> does). It listens to OBS's scene / scene-item events (program scene changed, a source
/// switched on or off, items re-ordered) and reads the scene's item list only when one arrives. Every ~150 ms it runs
/// <see cref="CamPlacer"/>; OBS hears from it only when a cam has to move: a 250 ms slide in five
/// SetSceneItemTransform steps (the cam and its gold frame together), or, when no slot is free, SetSceneItemIndex to lift
/// the cam (and frame) above the overlay - and back under it once clear.</item>
/// <item>It never switches a source on: only cams that are on are moved (the face cam is switched on and off by Angelo's
/// no-face-reveal watcher). A cam that is switched off while away from home is put back home at once (invisibly), so it
/// comes back where it belongs. "Starting soon" is never touched.</item>
/// <item>A cam found where Grandstand didn't put it (moved by hand in OBS) becomes that scene's new home for its role; the
/// slots are saved in control.json (cams.slots).</item>
/// </list>
/// </summary>
internal static class ObsCams
{
    // ------------------------------------------------------------ render thread side
    const int MaxRects = 48;
    static readonly SKRect[] pubRects = new SKRect[MaxRects];
    static int pubCount;
    static readonly object pubGate = new();
    static double testUntilMs = -1; static SKRect testRect;
    static readonly Stopwatch clock = Stopwatch.StartNew();

    /// <summary>Cost of the render thread's collection (ms): running average and worst, for /api/cams.</summary>
    public static double CollectAvgMs, CollectMaxMs; static long collectN;

    /// <summary>The graphics on screen now (1080 units). Called by the compositor every ~150 ms.</summary>
    public static void Publish(ReadOnlySpan<SKRect> rects, double collectMs)
    {
        lock (pubGate)
        {
            int n = Math.Min(rects.Length, MaxRects);
            rects[..n].CopyTo(pubRects);
            pubCount = n;
        }
        collectN++;
        CollectAvgMs += (collectMs - CollectAvgMs) / Math.Min(collectN, 200);
        if (collectMs > CollectMaxMs && collectN > 20) CollectMaxMs = collectMs;   // (first ones include JIT)
        Start();
    }

    /// <summary>The control app's test: a pretend graphic over the first cam on screen for 6 s - it moves away, then home
    /// once that is clear for 3 s.</summary>
    public static string Test()
    {
        Start();
        var s = state;
        var c = s.Cams.FirstOrDefault(x => x.Enabled && x.Role == "face") ?? s.Cams.FirstOrDefault(x => x.Enabled);
        if (!s.Linked) return "OBS isn't linked (is OBS open with its remote control on?)";
        if (c == null) return $"no cam is switched on in \"{s.Scene}\"";
        testRect = c.P.Cur; testUntilMs = clock.Elapsed.TotalMilliseconds + 6000;
        return $"covering {c.Name} in \"{s.Scene}\" for 6 s";
    }

    // ------------------------------------------------------------ status (control app)
    sealed class CamItem
    {
        public int Id; public string Name = "", Role = ""; public bool Enabled; public int Index;
        public int FrameId = -1; public int FrameIndex = -1; public int Align = 5, FrameAlign = 5;
        public SKRect Actual, FrameActual;
        public CamPlacer.Cam P = new();
    }
    sealed record Snapshot(bool Linked, string Scene, List<CamItem> Cams, string Note);
    static volatile Snapshot state = new(false, "", new(), "");
    public static int Calls, Moves, Raises;
    static string lastAction = "";

    public static object Describe()
    {
        var s = state;
        return new
        {
            on = ControlState.Current.Cams.KeepClear,
            linked = s.Linked, scene = s.Scene, note = s.Note,
            cams = s.Cams.Select(c => new
            {
                name = c.Name, role = c.Role, enabled = c.Enabled, raised = c.P.Raised,
                at = c.P.AtHome ? "home" : "away",
                x = c.P.Cur.Left, y = c.P.Cur.Top, w = c.P.Cur.Width, h = c.P.Cur.Height,
            }).ToList(),
            collectMs = Math.Round(CollectAvgMs, 4), collectMaxMs = Math.Round(CollectMaxMs, 4),
            calls = Calls, moves = Moves, raises = Raises, last = lastAction,
        };
    }

    // ------------------------------------------------------------ the loop
    static int started;
    static void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 1) return;
        Task.Run(Loop);
    }

    // per scene + item id (the cam's state survives scene switches)
    static readonly Dictionary<(string, int), CamItem> known = new();
    static volatile bool dirtyScene = true, dirtyItems = true;
    static readonly ConcurrentQueue<string> events = new();

    static async Task Loop()
    {
        ObsLink? link = null;
        DateTime nextTry = DateTime.MinValue;
        bool toldOff = false, wasOn = true;
        string scene = "";
        var items = new List<CamItem>();
        var gr = new SKRect[MaxRects + 1];
        var camArr = new CamPlacer.Cam[4];
        float scale = 2f, offX = 0, offY = 0;
        int overlayId = -1, overlayIndex = -1;
        while (true)
        {
            await Task.Delay(150);
            try
            {
                bool on = ControlState.Current.Cams.KeepClear;
                if (!on)
                {
                    if (wasOn && link is { IsOpen: true }) await PutBackAll(link, scene, items);   // switched off: every cam home, under the overlay
                    wasOn = false;
                    state = new Snapshot(link?.IsOpen == true, scene, items, "off");
                    await Task.Delay(850);
                    continue;
                }
                if (!wasOn) { wasOn = true; dirtyItems = true; }
                if (link is not { IsOpen: true })
                {
                    if (DateTime.UtcNow < nextTry) continue;
                    link?.Dispose(); link = null;
                    var cfg = ObsMarkers.ReadConfig();
                    if (cfg == null)
                    {
                        nextTry = DateTime.UtcNow.AddSeconds(20);
                        if (!toldOff) Program.Log("cams: can't use OBS's remote control (" + ObsMarkers.LastConfigProblem + ") - cams stay where OBS has them");
                        toldOff = true; state = new Snapshot(false, "", new(), ObsMarkers.LastConfigProblem); continue;
                    }
                    link = await ObsLink.Open(cfg, OnEvent);
                    if (link == null) { nextTry = DateTime.UtcNow.AddSeconds(10); state = new Snapshot(false, "", new(), "OBS isn't running or its remote control is off"); continue; }
                    toldOff = false; dirtyScene = dirtyItems = true;
                    Program.Log("cams: linked to OBS (keeping the face / pedal cams clear of the graphics)");
                }
                if (dirtyScene)
                {
                    dirtyScene = false;
                    var r = await Call(link, "GetCurrentProgramScene", null);
                    string sc = r.Data is { } d && d.TryGetProperty("sceneName", out var sn) ? sn.GetString() ?? "" : "";
                    if (sc != scene) { scene = sc; dirtyItems = true; }
                }
                if (dirtyItems)
                {
                    dirtyItems = false;
                    (items, overlayId, overlayIndex, scale, offX, offY) = await ReadScene(link, scene);
                    await Tidy(link, scene, items, overlayIndex);
                }
                var cams = items.Where(c => c.Enabled).ToList();
                state = new Snapshot(true, scene, items, cams.Count == 0 ? "no cam switched on in this scene" : "");
                if (scene == "Starting soon" || cams.Count == 0 || overlayId < 0) continue;

                // the graphics in canvas pixels
                int n;
                lock (pubGate)
                {
                    n = pubCount;
                    for (int i = 0; i < n; i++) { var q = pubRects[i]; gr[i] = new SKRect(offX + q.Left * scale, offY + q.Top * scale, offX + q.Right * scale, offY + q.Bottom * scale); }
                }
                double now = clock.Elapsed.TotalMilliseconds;
                if (now < testUntilMs) gr[n++] = testRect;
                int m = Math.Min(cams.Count, camArr.Length);
                for (int i = 0; i < m; i++) camArr[i] = cams[i].P;
                for (int i = 0; i < m; i++)
                {
                    var act = CamPlacer.Decide(camArr, m, i, gr.AsSpan(0, n), now, out var to);
                    if (act == CamPlacer.Act.None) continue;
                    var c = cams[i];
                    if (act == CamPlacer.Act.Move)
                    {
                        var from = c.P.Cur;
                        CamPlacer.Moved(c.P, to, now);
                        Moves++;
                        lastAction = $"{DateTime.Now:HH:mm:ss} {c.Name}: {(CamPlacer.Near(to, c.P.Home) ? "back home" : "moved clear")}";
                        Program.Log("cams: " + lastAction[9..] + $" in {scene}");
                        await Slide(link, scene, c, from, to);
                        if (c.P.Raised) { await Lower(link, scene, c); dirtyItems = true; }
                    }
                    else if (act == CamPlacer.Act.Raise)
                    {
                        await Raise(link, scene, c, overlayIndex);
                        c.P.Raised = true; Raises++; dirtyItems = true;
                        lastAction = $"{DateTime.Now:HH:mm:ss} {c.Name}: no free spot - over the graphics";
                        Program.Log("cams: " + lastAction[9..] + $" in {scene}");
                    }
                    else if (act == CamPlacer.Act.Lower)
                    {
                        await Lower(link, scene, c);
                        c.P.Raised = false; dirtyItems = true;
                        lastAction = $"{DateTime.Now:HH:mm:ss} {c.Name}: back under the graphics";
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Log("cams: " + ex.GetType().Name + " " + ex.Message);
                link?.Dispose(); link = null; nextTry = DateTime.UtcNow.AddSeconds(10);
            }
        }
    }

    static void OnEvent(string type, JsonElement d)
    {
        switch (type)
        {
            case "CurrentProgramSceneChanged": case "CurrentSceneCollectionChanged": case "SceneNameChanged":
                dirtyScene = true; dirtyItems = true; break;
            case "SceneItemEnableStateChanged": case "SceneItemListReindexed": case "SceneItemCreated": case "SceneItemRemoved":
                dirtyItems = true; break;
        }
    }

    static async Task<(bool Ok, JsonElement? Data)> Call(ObsLink link, string type, object? data)
    {
        Interlocked.Increment(ref Calls);
        return await link.Call(type, data);
    }

    static string RoleOf(string name, string kind)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("frame") || kind.StartsWith("color_source", StringComparison.Ordinal) || kind.Contains("audio") || kind.StartsWith("wasapi", StringComparison.Ordinal)) return "";
        if (n.Contains("face")) return "face";
        if (n.Contains("pedal")) return "pedal";
        return "";
    }

    /// <summary>The item's rect on the canvas from its transform (bounds box when it has one, else its scaled size), and
    /// its alignment (OBS: 1 left, 2 right, 4 top, 8 bottom; 0 centre).</summary>
    static SKRect RectOf(JsonElement t, out int align)
    {
        float F(string k) => t.TryGetProperty(k, out var v) && v.TryGetDouble(out var x) ? (float)x : 0f;
        align = t.TryGetProperty("alignment", out var al) && al.TryGetInt32(out var a) ? a : 5;
        string bt = t.TryGetProperty("boundsType", out var b) ? b.GetString() ?? "" : "";
        float w = bt is "" or "OBS_BOUNDS_NONE" ? F("width") : F("boundsWidth");
        float h = bt is "" or "OBS_BOUNDS_NONE" ? F("height") : F("boundsHeight");
        float x = F("positionX"), y = F("positionY");
        float l = (align & 1) != 0 ? x : (align & 2) != 0 ? x - w : x - w / 2f;
        float tp = (align & 4) != 0 ? y : (align & 8) != 0 ? y - h : y - h / 2f;
        return SKRect.Create(l, tp, w, h);
    }

    static SKPoint PosFor(SKRect r, int align) => new(
        (align & 1) != 0 ? r.Left : (align & 2) != 0 ? r.Right : r.MidX,
        (align & 4) != 0 ? r.Top : (align & 8) != 0 ? r.Bottom : r.MidY);

    static async Task<(List<CamItem>, int, int, float, float, float)> ReadScene(ObsLink link, string scene)
    {
        var list = new List<CamItem>();
        int overlayId = -1, overlayIndex = -1;
        float scale = 2f, offX = 0, offY = 0;
        if (scene.Length == 0) return (list, overlayId, overlayIndex, scale, offX, offY);
        var r = await Call(link, "GetSceneItemList", new { sceneName = scene });
        if (r.Data is not { } d || !d.TryGetProperty("sceneItems", out var arr)) return (list, overlayId, overlayIndex, scale, offX, offY);
        var frames = new List<(int Id, int Index, SKRect R, int Align)>();
        float canvasW = 3840, canvasH = 2160;
        foreach (var it in arr.EnumerateArray())
        {
            int id = it.GetProperty("sceneItemId").GetInt32();
            string name = it.TryGetProperty("sourceName", out var sn) ? sn.GetString() ?? "" : "";
            string kind = it.TryGetProperty("inputKind", out var ik) && ik.ValueKind == JsonValueKind.String ? ik.GetString() ?? "" : "";
            bool en = it.TryGetProperty("sceneItemEnabled", out var e) && e.ValueKind == JsonValueKind.True;
            int index = it.TryGetProperty("sceneItemIndex", out var ix) && ix.TryGetInt32(out var iv) ? iv : 0;
            if (!it.TryGetProperty("sceneItemTransform", out var tr)) continue;
            var rect = RectOf(tr, out int align);
            if (name == "Grandstand overlay" || (kind == "spout_capture" && name.Contains("Grandstand", StringComparison.OrdinalIgnoreCase)))
            {
                overlayId = id; overlayIndex = index;
                if (rect.Width > 100) { scale = rect.Width / 1920f; offX = rect.Left; offY = rect.Top; canvasW = rect.Width; canvasH = rect.Height; }
                continue;
            }
            if (kind.StartsWith("color_source", StringComparison.Ordinal) && name.Contains("frame", StringComparison.OrdinalIgnoreCase)) { frames.Add((id, index, rect, align)); continue; }
            string role = RoleOf(name, kind);
            if (role.Length == 0 || rect.Width < 16 || rect.Height < 16 || rect.Width >= canvasW * 0.95f) continue;
            if (!known.TryGetValue((scene, id), out var ci)) { ci = new CamItem { Id = id }; known[(scene, id)] = ci; }
            ci.Name = name; ci.Role = role; ci.Enabled = en; ci.Index = index; ci.Align = align; ci.Actual = rect;
            list.Add(ci);
        }
        foreach (var c in list)
        {
            // its gold frame: the colour source 6 px bigger all round (where it is now, or where it was at home)
            var want = new SKRect(c.Actual.Left - CamPlacer.FramePad, c.Actual.Top - CamPlacer.FramePad, c.Actual.Right + CamPlacer.FramePad, c.Actual.Bottom + CamPlacer.FramePad);
            var f = frames.FirstOrDefault(x => CamPlacer.Near(x.R, want, 3f));
            if (f.Id == 0 && c.FrameId >= 0) f = frames.FirstOrDefault(x => x.Id == c.FrameId);
            c.FrameId = f.Id == 0 ? -1 : f.Id; c.FrameIndex = f.Index; c.FrameAlign = f.Align; c.FrameActual = f.R;
            SetSlots(scene, c, canvasW, canvasH, list);
        }
        return (list, overlayId, overlayIndex, scale, offX, offY);
    }

    /// <summary>The cam's slots from control.json; a cam that is neither at its home nor an alternative was moved by hand
    /// (or the scene changed): that spot becomes the new home and the alternatives are worked out again (saved).</summary>
    static void SetSlots(string scene, CamItem c, float cw, float ch, List<CamItem> all)
    {
        var cs = ControlState.Current.Cams;
        var slot = cs.Slots.FirstOrDefault(x => x.Scene == scene && x.Role == c.Role);
        bool ours = c.P.Cur.Width > 0 && CamPlacer.Near(c.P.Cur, c.Actual, 3f);
        bool fits = slot != null && (CamPlacer.Near(slot.Home.Rect, c.Actual, 3f) || slot.Alts.Any(a => CamPlacer.Near(a.Rect, c.Actual, 3f)));
        if (slot == null || (!fits && !ours))
        {
            var others = all.Where(o => o != c).Select(o => o.P.Home.Width > 0 ? o.P.Home : o.Actual).ToArray();
            slot = CamDefaults.Make(scene, c.Role, c.Actual, others, cw, ch);
            SaveSlot(slot);
            Program.Log($"cams: {c.Name} in \"{scene}\" - new home {c.Actual.Left:0},{c.Actual.Top:0} {c.Actual.Width:0}x{c.Actual.Height:0}, {slot.Alts.Count} alternatives");
        }
        c.P.Name = c.Name;
        c.P.Home = slot.Home.Rect;
        c.P.Alts = slot.Alts.Select(a => a.Rect).ToArray();
        if (!ours) c.P.Cur = c.Actual;
    }

    static void SaveSlot(CamSlots slot)
    {
        // the whole settings, read - changed - written back (as the control app does)
        var s = JsonSerializer.Deserialize<ControlSettings>(JsonSerializer.Serialize(ControlState.Current, ControlState.Json), ControlState.Json)!;
        s.Cams.Slots.RemoveAll(x => x.Scene == slot.Scene && x.Role == slot.Role);
        s.Cams.Slots.Add(slot);
        ControlState.Replace(s);
    }

    /// <summary>After reading a scene: a cam switched off while away goes home at once (unseen); a cam left above the
    /// overlay by an earlier run goes back under it.</summary>
    static async Task Tidy(ObsLink link, string scene, List<CamItem> items, int overlayIndex)
    {
        foreach (var c in items)
        {
            if (!c.Enabled && !c.P.AtHome && c.P.Home.Width > 0)
            {
                await Place(link, scene, c, c.P.Home);
                c.P.Cur = c.P.Home; c.P.HomeFreeSince = -1;
            }
            if (!c.P.Raised && overlayIndex >= 0 && c.Index > overlayIndex) { await Lower(link, scene, c); dirtyItems = true; }
            if (!c.Enabled && c.P.Raised) { await Lower(link, scene, c); c.P.Raised = false; dirtyItems = true; }
        }
    }

    static async Task PutBackAll(ObsLink link, string scene, List<CamItem> items)
    {
        foreach (var c in items)
        {
            if (!c.P.AtHome && c.P.Home.Width > 0) { await Place(link, scene, c, c.P.Home); c.P.Cur = c.P.Home; }
            if (c.P.Raised) { await Lower(link, scene, c); c.P.Raised = false; }
        }
        dirtyItems = true;
    }

    /// <summary>The 250 ms slide: five steps, eased (the session bar's smoothstep).</summary>
    static async Task Slide(ObsLink link, string scene, CamItem c, SKRect from, SKRect to)
    {
        const int Steps = 5;
        for (int s = 1; s <= Steps; s++)
        {
            float k = s / (float)Steps; k = k * k * (3 - 2 * k);
            var r = SKRect.Create(from.Left + (to.Left - from.Left) * k, from.Top + (to.Top - from.Top) * k, to.Width, to.Height);
            long t0 = Stopwatch.GetTimestamp();
            await Place(link, scene, c, r);
            if (s < Steps) { int left = 50 - (int)Stopwatch.GetElapsedTime(t0).TotalMilliseconds; if (left > 0) await Task.Delay(left); }
        }
    }

    static async Task Place(ObsLink link, string scene, CamItem c, SKRect r)
    {
        var p = PosFor(r, c.Align);
        await Call(link, "SetSceneItemTransform", new { sceneName = scene, sceneItemId = c.Id, sceneItemTransform = new { positionX = Math.Round(p.X, 1), positionY = Math.Round(p.Y, 1) } });
        if (c.FrameId >= 0)
        {
            var f = new SKRect(r.Left - CamPlacer.FramePad, r.Top - CamPlacer.FramePad, r.Right + CamPlacer.FramePad, r.Bottom + CamPlacer.FramePad);
            var fp = PosFor(f, c.FrameAlign);
            await Call(link, "SetSceneItemTransform", new { sceneName = scene, sceneItemId = c.FrameId, sceneItemTransform = new { positionX = Math.Round(fp.X, 1), positionY = Math.Round(fp.Y, 1) } });
        }
    }

    /// <summary>Above the overlay: the frame to the overlay's place, then the cam to the same place (order: overlay, frame,
    /// cam).</summary>
    static async Task Raise(ObsLink link, string scene, CamItem c, int overlayIndex)
    {
        if (overlayIndex < 0) return;
        if (c.FrameId >= 0 && c.FrameIndex < overlayIndex) await Call(link, "SetSceneItemIndex", new { sceneName = scene, sceneItemId = c.FrameId, sceneItemIndex = overlayIndex });
        await Call(link, "SetSceneItemIndex", new { sceneName = scene, sceneItemId = c.Id, sceneItemIndex = overlayIndex });
    }

    /// <summary>Back under the overlay (frame first, then the cam, both just below it - the order the scene was built in).</summary>
    static async Task Lower(ObsLink link, string scene, CamItem c)
    {
        var (_, ov, _, _, _, _) = await ReadIndexes(link, scene, c);
        if (ov < 0) return;
        if (c.FrameId >= 0)
        {
            await Call(link, "SetSceneItemIndex", new { sceneName = scene, sceneItemId = c.FrameId, sceneItemIndex = ov });
            (_, ov, _, _, _, _) = await ReadIndexes(link, scene, c);
            if (ov < 0) return;
        }
        int camIdx = (await ReadIndexes(link, scene, c)).Cam;
        if (camIdx > ov) await Call(link, "SetSceneItemIndex", new { sceneName = scene, sceneItemId = c.Id, sceneItemIndex = ov });
    }

    static async Task<(int Cam, int Overlay, int Frame, int a, int b, int d)> ReadIndexes(ObsLink link, string scene, CamItem c)
    {
        var r = await Call(link, "GetSceneItemList", new { sceneName = scene });
        int cam = -1, ov = -1, fr = -1;
        if (r.Data is { } d && d.TryGetProperty("sceneItems", out var arr))
            foreach (var it in arr.EnumerateArray())
            {
                int id = it.GetProperty("sceneItemId").GetInt32();
                int ix = it.TryGetProperty("sceneItemIndex", out var x) && x.TryGetInt32(out var v) ? v : -1;
                string name = it.TryGetProperty("sourceName", out var sn) ? sn.GetString() ?? "" : "";
                if (id == c.Id) cam = ix;
                else if (id == c.FrameId) fr = ix;
                else if (name == "Grandstand overlay") ov = ix;
            }
        return (cam, ov, fr, 0, 0, 0);
    }
}

/// <summary>#611: a small obs-websocket v5 client of its own (the cam placer must not wait behind the scene director's
/// or the highlight reel's requests): requests are matched to their answers by id, events go to a callback, a reader
/// task owns the socket's receive side.</summary>
internal sealed class ObsLink : IDisposable
{
    readonly ClientWebSocket ws;
    readonly ConcurrentDictionary<string, TaskCompletionSource<(bool, JsonElement?)>> waiting = new();
    readonly Action<string, JsonElement> onEvent;
    readonly SemaphoreSlim sendGate = new(1, 1);
    int id;
    volatile bool closed;
    public bool IsOpen => !closed && ws.State == WebSocketState.Open;

    ObsLink(ClientWebSocket ws, Action<string, JsonElement> onEvent) { this.ws = ws; this.onEvent = onEvent; }

    // General (1) | Config (2) | Scenes (4) | SceneItems (128): scene switches, collections, items on / off / re-ordered
    const int Subscriptions = 1 | 2 | 4 | 128;

    public static async Task<ObsLink?> Open(ObsMarkers.ObsConfig cfg, Action<string, JsonElement> onEvent)
    {
        var ws = new ClientWebSocket();
        try
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4))) await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{cfg.Port}"), cts.Token);
            var buf = new byte[64 * 1024];
            using var hello = await Receive(ws, buf, 5000);
            var d = hello.RootElement.GetProperty("d");
            string? auth = null;
            if (d.TryGetProperty("authentication", out var a))
            {
                if (string.IsNullOrEmpty(cfg.Password)) { ws.Dispose(); return null; }
                auth = ObsMarkers.AuthString(cfg.Password, a.GetProperty("salt").GetString() ?? "", a.GetProperty("challenge").GetString() ?? "");
            }
            object ident = auth == null ? new { rpcVersion = 1, eventSubscriptions = Subscriptions } : new { rpcVersion = 1, authentication = auth, eventSubscriptions = Subscriptions };
            await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { op = 1, d = ident }), WebSocketMessageType.Text, true, CancellationToken.None);
            using var ok = await Receive(ws, buf, 5000);
            if (ok.RootElement.GetProperty("op").GetInt32() != 2) { ws.Dispose(); return null; }
            var link = new ObsLink(ws, onEvent);
            _ = Task.Run(() => link.Reader(buf));
            return link;
        }
        catch { ws.Dispose(); return null; }
    }

    static async Task<JsonDocument> Receive(ClientWebSocket ws, byte[] buf, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do { r = await ws.ReceiveAsync(buf, cts.Token); if (r.MessageType == WebSocketMessageType.Close) throw new WebSocketException("closed"); ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
        ms.Position = 0;
        return JsonDocument.Parse(ms);
    }

    async Task Reader(byte[] buf)
    {
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do { r = await ws.ReceiveAsync(buf, CancellationToken.None); if (r.MessageType == WebSocketMessageType.Close) throw new WebSocketException("closed"); ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
                ms.Position = 0;
                using var doc = JsonDocument.Parse(ms);
                var root = doc.RootElement;
                int op = root.GetProperty("op").GetInt32();
                var d = root.GetProperty("d");
                if (op == 7)
                {
                    string rid = d.GetProperty("requestId").GetString() ?? "";
                    if (waiting.TryRemove(rid, out var tcs))
                    {
                        bool ok = d.GetProperty("requestStatus").GetProperty("result").GetBoolean();
                        tcs.TrySetResult((ok, ok && d.TryGetProperty("responseData", out var rd) ? rd.Clone() : null));
                    }
                }
                else if (op == 5)
                {
                    string type = d.GetProperty("eventType").GetString() ?? "";
                    var data = d.TryGetProperty("eventData", out var ed) ? ed.Clone() : default;
                    try { onEvent(type, data); } catch { }
                }
            }
        }
        catch { }
        closed = true;
        foreach (var kv in waiting) kv.Value.TrySetResult((false, null));
        waiting.Clear();
    }

    public async Task<(bool Ok, JsonElement? Data)> Call(string type, object? data, int timeoutMs = 3000)
    {
        if (!IsOpen) throw new WebSocketException("OBS link closed");
        string rid = "gc" + Interlocked.Increment(ref id);
        var tcs = new TaskCompletionSource<(bool, JsonElement?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting[rid] = tcs;
        var msg = JsonSerializer.SerializeToUtf8Bytes(new { op = 6, d = data == null ? (object)new { requestType = type, requestId = rid } : new { requestType = type, requestId = rid, requestData = data } });
        await sendGate.WaitAsync();
        try { await ws.SendAsync(msg, WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { sendGate.Release(); }
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (done != tcs.Task) { waiting.TryRemove(rid, out _); throw new TimeoutException("OBS didn't answer " + type); }
        return tcs.Task.Result;
    }

    public void Dispose()
    {
        closed = true;
        try { ws.Abort(); } catch { }
        try { ws.Dispose(); } catch { }
    }
}
