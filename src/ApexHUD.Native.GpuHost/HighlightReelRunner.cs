using System.IO;
using ApexHUD.Native.Common;
using ApexHUD.Native.Data;
using ApexHUD.Native.Overlays;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// The highlight reel (2026-10-29, #9 phase 1, Angelo: "after a session, one click and Grandstand makes the highlight
/// video"). Plays a session's highlight markers back in iRacing's replay, one moment after another, while OBS records
/// them into one ready-made video with a chapter per moment.
///
/// <para><b>How.</b> The plan comes from <see cref="HighlightReel.Build"/> (pure, tested in tools/reel_check): up to 20
/// moments in time order, each starting 6 s before its marker and playing 12 s (passes, lost places, battles) or 8 s.
/// For each moment, through the SDK broadcast messages: pause the replay, jump to the start
/// (ReplaySearchSessionTime), point the camera at the marker's first car with a TV camera group
/// (<see cref="HighlightReel.PickGroup"/>: TV1 / TV2 / TV3 in turn, Pit Lane for pit stops, then Chase), wait for the
/// camera to take, then play at 1x until the replay clock reaches the end. OBS starts recording on the first moment and
/// stops after the last; while the replay jumps between moments the recording is paused (PauseRecord / ResumeRecord),
/// so the video has no frozen frames or loading flashes - only when the reel started the recording itself. If OBS was
/// already recording, the reel only adds chapters and leaves that recording running.</para>
///
/// <para><b>Safety.</b> Starts only while iRacing is in a replay (a replay file, or a live session watched behind live)
/// and you are not in the car. Stops at once if you get in the car, iRacing goes away, the replay goes back to live, or
/// you take over (the camera car / camera group / play speed changed, or the replay jumped, not by the reel). Stop
/// button + POST /api/reel/stop. Nothing runs until clicked; while it plays the highlight log writes no new markers and
/// the replay director keeps its hands off the camera (<see cref="HighlightReel.Playing"/>).</para>
///
/// <para><b>4K.</b> The video is whatever OBS captures. For a true 4K reel, watch the replay in iRacing's monitor mode
/// on the 4K screen (the future "Watch Replay" mode) with OBS capturing that screen at 3840x2160; this runner does not
/// switch iRacing's display mode.</para>
/// </summary>
internal sealed class HighlightReelRunner : IDisposable
{
    readonly RaceStateService rs;
    readonly object gate = new();
    Thread? thread;
    readonly ManualResetEventSlim stopEvent = new(false);
    volatile bool stopRequested;
    string stopWhy = "";

    // status for the control page
    public string State { get; private set; } = "Idle";
    public string Why { get; private set; } = "Pick a session and press Start while iRacing shows its replay.";
    public string File { get; private set; } = "";
    public int Index { get; private set; } = -1;
    public int Total { get; private set; }
    public string Label { get; private set; } = "";
    public string Saved { get; private set; } = "";
    public bool Running => thread is { IsAlive: true };

    public HighlightReelRunner(RaceStateService rs) => this.rs = rs;

    /// <summary>The highlight files, newest first (the control app's session picker).</summary>
    public static IReadOnlyList<(string File, string Label, int Markers, int Moments, double Seconds, DateTime Written)> Sessions(int max = 30)
    {
        var list = new List<(string, string, int, int, double, DateTime)>();
        try
        {
            if (!Directory.Exists(HighlightLog.Folder)) return list;
            foreach (var f in new DirectoryInfo(HighlightLog.Folder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Take(max))
            {
                try
                {
                    var marks = HighlightReel.ParseMarkers(System.IO.File.ReadAllText(f.FullName));
                    var plan = HighlightReel.Build(marks);
                    list.Add((f.Name, HighlightReel.SessionLabel(f.Name), marks.Count, plan.Count, HighlightReel.TotalSeconds(plan), f.LastWriteTime));
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    public object Describe() => new
    {
        running = Running,
        state = State,
        why = Why,
        file = File,
        moment = Index + 1,
        total = Total,
        label = Label,
        saved = Saved,
    };

    /// <summary>Starts the reel for <paramref name="file"/> (a file name in the highlights folder; null/empty = the
    /// newest). Returns false with the reason when it can't start.</summary>
    public bool Start(string? file, out string message)
    {
        lock (gate)
        {
            if (Running) { message = "The reel is already playing."; return false; }
            if (string.IsNullOrEmpty(file)) file = Sessions(1).FirstOrDefault().File;
            if (string.IsNullOrEmpty(file)) { message = "No highlight files yet (Documents\\iRacing\\Grandstand\\highlights)."; return false; }
            if (!HighlightReel.SafeFileName(file)) { message = "Not a highlight file name."; return false; }
            string path = Path.Combine(HighlightLog.Folder, file);
            if (!System.IO.File.Exists(path)) { message = "That highlight file isn't there any more."; return false; }
            List<HighlightReel.Moment> plan;
            try { plan = HighlightReel.Build(HighlightReel.ParseMarkers(System.IO.File.ReadAllText(path))); }
            catch (Exception ex) { message = "Couldn't read the highlight file (" + ex.Message + ")."; return false; }
            if (plan.Count == 0) { message = "That session has no moments with a replay time."; return false; }
            if (!SafeToSteer(starting: true, out var why)) { message = why; State = "Idle"; Why = why; return false; }

            File = file; Total = plan.Count; Index = -1; Label = ""; Saved = ""; stopWhy = "";
            stopRequested = false; stopEvent.Reset();
            State = "Starting"; Why = $"{plan.Count} moments, about {Math.Round(HighlightReel.TotalSeconds(plan))} s of video.";
            HighlightReel.Playing = true;
            Program.Log($"reel: starting {file} - {plan.Count} moments, {HighlightReel.TotalSeconds(plan):0} s");
            thread = new Thread(() => Run(plan)) { IsBackground = true, Name = "Grandstand-Reel", Priority = ThreadPriority.BelowNormal };
            thread.Start();
            message = Why;
            return true;
        }
    }

    /// <summary>Stops the reel (Stop button / POST /api/reel/stop). The recording made so far is kept.</summary>
    public void Stop(string why = "Stopped by you.")
    {
        if (!Running) return;
        stopWhy = why;
        stopRequested = true;
        stopEvent.Set();
    }

    // ---------------------------------------------------------------- the run (its own thread)

    bool recordingOurs, recordingAny;

    void Run(List<HighlightReel.Moment> plan)
    {
        recordingOurs = recordingAny = false;
        bool paused = false;
        try
        {
            var groups = rs.CameraGroups().Select(g => (g.Num, g.Name)).ToList();
            if (groups.Count == 0) { Wait(1000); groups = rs.CameraGroups().Select(g => (g.Num, g.Name)).ToList(); }
            Program.Log($"reel: {groups.Count} camera groups ({string.Join(", ", groups.Select(g => g.Name))})");
            for (int i = 0; i < plan.Count; i++)
            {
                if (stopRequested) break;
                var m = plan[i];
                Index = i; Label = m.Chapter;
                State = "Playing"; Why = $"Moment {i + 1} of {plan.Count}: {m.Chapter}";
                int car = ResolveCar(m.CarIdx);
                int group = HighlightReel.PickGroup(groups, m.Kind, i);
                var nums = rs.CarNumbersRaw();
                if (car < 0 || !nums.TryGetValue(car, out int raw)) { Program.Log($"reel: moment {i + 1} skipped - car {m.CarIdx} not in this session"); continue; }
                string gname = groups.FirstOrDefault(g => g.Num == group).Name ?? "";
                Program.Log($"reel: moment {i + 1}/{plan.Count} {m.Kind} \"{m.Chapter}\" session {m.SessionNum} @ {m.StartTime:0.0}s for {m.PlaySeconds:0.0}s, car {car} (#{raw}), camera {(group >= 0 ? gname : "keep")}{(m.Seek ? "" : ", carries on (no jump)")}");

                // 1. get there with the replay paused (and the recording paused, when it's ours and running)
                if (m.Seek || i == 0)
                {
                    if (recordingOurs && !paused) { paused = Obs(ObsMarkers.PauseRecordAsync(), "pause"); }
                    rs.ReplaySpeed(0, false);
                    rs.ReplayToSessionTime(m.SessionNum, (int)(m.StartTime * 1000));
                    if (!WaitForTime(m.SessionNum, m.StartTime)) { if (stopRequested) break; Program.Log("reel: the replay didn't reach the moment in 10 s - playing from where it is"); }
                }
                if (stopRequested) break;
                // 2. the camera, re-sent until it takes (iRacing resets the camera after a jump)
                rs.CamToCar(raw, Math.Max(0, group));
                if (!WaitForCamera(car, group, raw)) { if (stopRequested) break; Program.Log("reel: the camera didn't take - carrying on with iRacing's"); }
                if (!SafeToSteer(false, out var why0)) { StopFor(why0); break; }
                // 3. recording: started on the first moment; resumed after a jump
                if (i == 0)
                {
                    var r = ObsMarkers.StartRecordAsync().GetAwaiter().GetResult();
                    recordingAny = r.Ok;
                    recordingOurs = r.Ok && !r.WasRecording;
                    if (!r.Ok) { Program.Log("reel: OBS isn't recording (" + r.Problem + ") - playing the reel anyway"); }
                    else if (r.WasRecording) Program.Log("reel: OBS was already recording - adding chapters to that recording, leaving it running");
                }
                else if (paused) { Obs(ObsMarkers.ResumeRecordAsync(), "resume"); paused = false; }
                if (recordingAny) _ = ObsMarkers.ChapterAsync(m.Chapter);
                // 4. play at 1x until the replay clock reaches the end of the moment
                rs.ReplaySpeed(1, false);
                if (!Play(m, car, group)) break;
            }
            if (!stopRequested) { State = "Done"; Why = $"All {plan.Count} moments played."; Program.Log("reel: done"); rs.ReplaySpeed(0, false); }
        }
        catch (Exception ex) { StopFor("Error: " + ex.Message); Program.Log("reel error: " + ex); }
        finally
        {
            try
            {
                if (recordingOurs)
                {
                    if (paused) ObsMarkers.ResumeRecordAsync().GetAwaiter().GetResult();   // OBS won't stop cleanly while paused
                    var r = ObsMarkers.StopRecordAsync().GetAwaiter().GetResult();
                    if (r.Ok && r.OutputPath.Length > 0) { Saved = r.OutputPath; Why += " Saved: " + r.OutputPath; }
                    else if (!r.Ok) Why += " (OBS: " + r.Problem + ")";
                }
                else if (!recordingAny) Why += " Nothing was recorded (OBS not linked).";
            }
            catch { }
            if (stopRequested) { State = "Stopped"; Why = stopWhy + (Saved.Length > 0 ? " Saved: " + Saved : ""); Program.Log("reel: stopped - " + stopWhy); }
            HighlightReel.Playing = false;
        }
    }

    static bool Obs(Task<ObsMarkers.ReelResult> t, string what)
    {
        var r = t.GetAwaiter().GetResult();
        if (!r.Ok) Program.Log($"reel: OBS couldn't {what} the recording ({r.Problem})");
        return r.Ok;
    }

    void StopFor(string why)
    {
        stopWhy = why;
        stopRequested = true;
        stopEvent.Set();
    }

    bool Wait(int ms) => !stopEvent.Wait(ms);

    /// <summary>The car for a moment: the marker's own car, else your car, else the leader, else whoever is on camera.</summary>
    int ResolveCar(int carIdx)
    {
        var nums = rs.CarNumbersRaw();
        if (carIdx >= 0 && nums.ContainsKey(carIdx)) return carIdx;
        var s = rs.Current;
        foreach (int c in new[] { s.Player?.CarIdx ?? -1, s.PlayerCarIdx, s.Leader?.CarIdx ?? -1, rs.ReadInt("CamCarIdx") })
            if (c >= 0 && nums.ContainsKey(c)) return c;
        return -1;
    }

    bool WaitForTime(int sessionNum, double t)
    {
        long until = Environment.TickCount64 + 10000;
        if (!Wait(300)) return false;
        while (Environment.TickCount64 < until)
        {
            if (!SafeToSteer(false, out var why)) { StopFor(why); return false; }
            double now = rs.ReadDouble("SessionTime");
            if (rs.ReadInt("SessionNum") == sessionNum && !double.IsNaN(now) && Math.Abs(now - t) < 2.0) return Wait(250);   // a beat for the picture
            if (!Wait(100)) return false;
        }
        return false;
    }

    bool WaitForCamera(int car, int group, int raw)
    {
        long start = Environment.TickCount64, lastSend = start;
        while (Environment.TickCount64 - start < 2500)
        {
            if (!Wait(150)) return false;
            bool carOk = rs.ReadInt("CamCarIdx") == car;
            bool groupOk = group < 0 || rs.ReadInt("CamGroupNumber") == group;
            if (carOk && groupOk) return Wait(200);
            if (Environment.TickCount64 - lastSend > 700) { rs.CamToCar(raw, Math.Max(0, group)); lastSend = Environment.TickCount64; }
        }
        return false;
    }

    /// <summary>Plays one moment. False when the reel must stop (you took over, the replay went live, iRacing went away).</summary>
    bool Play(HighlightReel.Moment m, int car, int group)
    {
        long start = Environment.TickCount64;
        long cap = (long)((m.PlaySeconds + 8) * 1000);
        int jumps = -1;
        while (Environment.TickCount64 - start < cap)
        {
            if (!Wait(100)) return false;
            if (!SafeToSteer(false, out var why)) { StopFor(why); return false; }
            long held = Environment.TickCount64 - start;
            if (held < 1200) continue;                                    // the speed change settling
            if (jumps < 0) jumps = ReplayState.JumpCount;
            string? taken = null;
            if (rs.ReadInt("CamCarIdx") != car) taken = "the camera car changed";
            else if (group >= 0 && rs.ReadInt("CamGroupNumber") != group) taken = "the camera changed";
            else if (rs.ReadInt("ReplayPlaySpeed", 0, 1) != 1 || rs.ReadBool("ReplayPlaySlowMotion")) taken = "the replay speed changed";
            else if (ReplayState.JumpCount != jumps) taken = "the replay jumped";
            if (taken != null) { Program.Log("reel: you took over (" + taken + ") - stopping"); StopFor("Stopped: you took over (" + taken + ")."); return false; }
            double now = rs.ReadDouble("SessionTime");
            if (!double.IsNaN(now) && now >= m.EndTime) return true;
        }
        Program.Log("reel: moment ran long - moving on");
        return true;
    }

    long notReplaySinceMs = -1;

    /// <summary>The reel may steer: connected, in a replay, you not in the car. While running, leaving the replay must
    /// last 2 s (a jump can blink IsReplayPlaying); getting in the car or losing iRacing stops at once.</summary>
    bool SafeToSteer(bool starting, out string why)
    {
        why = "";
        if (!rs.SdkReady || !rs.IsSessionLive) { why = starting ? "iRacing isn't running - open the session's replay first." : "Stopped: iRacing went away."; return false; }
        bool replayFile = rs.SimMode.Equals("replay", StringComparison.OrdinalIgnoreCase);
        bool onTrack = rs.ReadBool("IsOnTrack");
        if (DrivingState.DrivingLive || (onTrack && !replayFile))
        { why = starting ? "You're in the car - the reel only plays from the replay, never while you drive." : "Stopped: you got in the car."; return false; }
        bool replay = replayFile || rs.ReadBool("IsReplayPlaying") || ReplayDirector.FramesBehind(rs) > 30;
        if (replay) { notReplaySinceMs = -1; return true; }
        if (starting) { why = "iRacing isn't in a replay - open the replay (or a replay file) first."; return false; }
        if (notReplaySinceMs < 0) notReplaySinceMs = Environment.TickCount64;
        if (Environment.TickCount64 - notReplaySinceMs > 2000) { why = "Stopped: the replay went back to live."; return false; }
        return true;
    }

    public void Dispose()
    {
        Stop("Grandstand is closing.");
        try { thread?.Join(3000); } catch { }
        HighlightReel.Playing = false;
    }
}
