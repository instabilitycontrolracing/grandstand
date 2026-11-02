// 2026-10-30 (#608 / #611 / #619): previews of the race-control bar, the "latest" viewer plate and the cam placer, drawn by
// the REAL renderers (RaceControlBar, ViewerLatestPlate, CamPlacer) over the procedural trackside picture.
//   theme_preview rcbar   OUT_DIR BG.png     4K stills: regular / battle / onboard, the six kinds, with the radio card up
//   theme_preview latest  OUT_DIR BG.png     4K stills: the plate (Super Chat, chat, member), with the race-control bar too
//   theme_preview rcclip  OUT.mp4 BG.png     1080p clip: the bar slides in, a battle starts (it glides under the ticker), out
//   theme_preview camsim  OUT.mp4 BG.png     1080p clip: the cams as labelled boxes at Angelo's OBS spots ("Racing"),
//                                            a story card comes in, the cams move clear, and go home 3 s after it leaves
#if !OLDBUILD
using System.Diagnostics;
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

static class CamsPreview
{
    sealed class Stage
    {
        public readonly TimingTowerRenderer Tower = new();
        public readonly SessionBarRenderer Bar = new();
        public readonly WatchRenderer Watch = new();
        public readonly StandingsTickerRenderer Ticker = new();
        public readonly RadioPopupRenderer Radio = new();
        public readonly RaceControlBar Rc = new();
        public readonly ViewerLatestPlate Plate = new();
        public readonly OnboardFullRenderer Onboard = new();
        public readonly StoryCards Story = new();
        public readonly FeaturedCardRenderer Featured = new();
        public DateTime Clock0 = new(2026, 10, 30, 20, 41, 0);

        public void Bar1(SKCanvas c, RaceSnapshot s, double t, float width)
        {
            var clock = Clock0.AddMilliseconds(t);
            SessionBarRenderer.TestClock = () => clock;
            float bx = (1920 - width) / 2f;
            Bar.Draw(c, bx, 18f, 1f, s, clock, t, width);
            Watch.Draw(c, bx + width / 2f, 18f + SessionBarRenderer.BarHeight / 2f, 64f, clock);
            VpLayout.SessionBar = new SKRect(bx, 18f, bx + width, 18f + SessionBarRenderer.BarHeight);
        }

        public void RadioCard(SKCanvas c, RaceSnapshot s, double t)
        {
            float rx = 1920 - 24f, by = 1080 * 0.665f;
            c.Save(); c.Translate(rx, by); c.Scale(0.9f); c.Translate(-rx, -by);
            Radio.Draw(c, rx, by, s, t);
            c.Restore();
        }
    }

    static void Bg(SKCanvas c, SKImage bg, float scale) => c.DrawImage(bg, new SKRect(0, 0, 1920 * scale, 1080 * scale));

    static void SaveJpg(SKSurface surf, string path, SKRectI? crop = null)
    {
        using var img = surf.Snapshot();
        using var sub = crop is { } r ? img.Subset(r) : null;
        using var d = (sub ?? img).Encode(crop != null ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, crop != null ? 100 : 90);
        File.WriteAllBytes(path, d.ToArray());
        Console.WriteLine("ok " + path);
    }

    /// <summary>Plays the scene forward on a no-draw canvas up to t (state machines keep the same clock), then draws t.</summary>
    static void Play(Action<SKCanvas, double> frame, double from, double to, SKCanvas real, float scale)
    {
        using var nd = new SKNoDrawCanvas(1920, 1080);
        for (double q = from; q < to; q += 16.7) frame(nd, q);
        real.Save(); real.Scale(scale); frame(real, to); real.Restore(); real.Flush();
    }

    static RaceSnapshot OnboardSnap(RaceSnapshot s)
    {
        var me = s.Player ?? s.ClassLeaderboard[0].Drivers[0];
        var ob = new OnboardData(me.CarIdx, 11, "CamRoll Bar", true, true, true, 0.82f, 0f, 0f, -0.12f, 4.7f, 52f, 3, 7200f, 7600f, 0f, false,
            LapPct: 0.30f, HasG: true, LatAccel: 6f, LongAccel: 3f);
        return s with { TrackRawName = "lemans full", Onboard = ob, PlayerOnboard = ob, Extras = s.Extras with { TrackLength = "13.5541 km" } };
    }

    static RcMessage Decision(RaceSnapshot s, RcCategory cat, string text, int inc = 4)
    {
        var d = s.Player ?? s.ClassLeaderboard[0].Drivers[0];
        return new RcMessage(cat, "L12", "20:41", d.CarNumber, NameFormat.Broadcast(d), d.ClassName, d.ClassColor, text, inc, 17);
    }

    // ------------------------------------------------------------------------------------------------ race-control bar
    public static void RcStills(string outDir, string bgPath, RaceSnapshot snap)
    {
        // the corner names come from CrewChief's trackLandmarksData.json (GS_LANDMARKS), as in tools/storycard_preview
        if (Environment.GetEnvironmentVariable("GS_LANDMARKS") is { Length: > 0 } lm) TrackCorners.LandmarkFileOverride = lm;
        Directory.CreateDirectory(outDir);
        using var bg = SKImage.FromEncodedData(bgPath);
        using var surf = SKSurface.Create(new SKImageInfo(3840, 2160, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surf.Canvas;

        // 1. regular: under the session bar plate, the team radio card up at the same time
        {
            var st = new Stage();
            RadioFeed.Push("ENGINEER", "Box this lap, box this lap. Fuel only, tyres are fine.");
            RaceControlBar.Post(Decision(snap, RcCategory.TrackLimits, "OFF TRACK AT THE FORD CHICANES, +1 INCIDENT POINT"));
            c.Clear(); Bg(c, bg, 2);
            Play((cv, t) =>
            {
                PresentationState.IsBattleActive = false; PresentationState.Tick(snap);
                st.Tower.Draw(cv, 16f, 18f, 1080 - 34f, snap, t);
                st.RadioCard(cv, snap, t);
                st.Rc.Draw(cv, 960f, 110f, 1f, 1920f, t);
                st.Bar1(cv, snap, t, SessionBarRenderer.BarWidth);
            }, 0, 2600, c, 2);
            SaveJpg(surf, Path.Combine(outDir, "01_race_control_bar_regular.jpg"));
            SaveJpg(surf, Path.Combine(outDir, "01b_regular_closeup.png"), new SKRectI(900, 0, 2940, 360));
        }
        // 2. battle: full-width session bar + ticker, the bar under the ticker
        {
            var st = new Stage();
            RaceControlBar.PushSpoken("[[0]]Blue flags are being shown to car 81 in turn 1.[[end=6]]");
            c.Clear(); Bg(c, bg, 2);
            Play((cv, t) =>
            {
                PresentationState.IsBattleActive = true;
                st.Rc.Draw(cv, 960f, 154f, 0f, 1920f, t);
                st.Ticker.Draw(cv, 0f, 110f, 1920, snap, t);
                st.Bar1(cv, snap, t, 1920f);
            }, 0, 2600, c, 2);
            SaveJpg(surf, Path.Combine(outDir, "02_race_control_bar_battle.jpg"));
            SaveJpg(surf, Path.Combine(outDir, "02b_battle_closeup.png"), new SKRectI(900, 0, 2940, 440));
            PresentationState.IsBattleActive = false;
        }
        // 3. onboard: under the corner / straight name
        {
            var st = new Stage();
            var os = OnboardSnap(snap);
            RaceControlBar.Post(Decision(os, RcCategory.Penalty, "DRIVE THROUGH PENALTY: SPEEDING IN THE PIT LANE"));
            c.Clear(); Bg(c, bg, 2);
            float placeBottom = 0;
            Play((cv, t) =>
            {
                st.Onboard.Feed(os, t);
                st.Onboard.Draw(cv, 1920, 1080, os, t, 1f);
                placeBottom = OnboardFullRenderer.PlaceBottom;
                st.Rc.Draw(cv, 960f, placeBottom > 0 ? placeBottom + 12f : 24f, 0f, 1920f, t);
            }, 0, 3000, c, 2);
            Console.WriteLine($"onboard: place name bottom {placeBottom:0.0} -> bar top {placeBottom + 12f:0.0}");
            SaveJpg(surf, Path.Combine(outDir, "03_race_control_bar_onboard.jpg"));
            SaveJpg(surf, Path.Combine(outDir, "03b_onboard_closeup.png"), new SKRectI(700, 0, 3140, 520));
        }
        // 4. the six kinds, one under the other (each its own bar)
        {
            c.Clear(); Bg(c, bg, 2);
            var msgs = new (string Label, Action Push)[]
            {
                ("info", () => RaceControlBar.PushSpoken("Pit exit is now open.")),
                ("yellow", () => RaceControlBar.PushSpoken("Full course yellow. Pace car is out, no overtaking.")),
                ("blue", () => RaceControlBar.PushSpoken("Blue flag for car 81, let the leaders through.")),
                ("track limits", () => RaceControlBar.Post(Decision(snap, RcCategory.TrackLimits, "OFF TRACK AT INDIANAPOLIS, +1 INCIDENT POINT", 3))),
                ("penalty", () => RaceControlBar.Post(Decision(snap, RcCategory.Penalty, "STOP AND GO PENALTY: AVOIDABLE CONTACT", 9))),
                ("black flag", () => RaceControlBar.Post(Decision(snap, RcCategory.Penalty, "BLACK FLAG: SERVE THE PENALTY IN THE PIT LANE", 12))),
            };
            for (int i = 0; i < msgs.Length; i++)
            {
                var rc = new RaceControlBar();
                msgs[i].Push();
                float top = 60f + i * 70f;
                using var nd = new SKNoDrawCanvas(1920, 1080);
                for (double q = 0; q < 2600; q += 16.7) rc.Draw(nd, 960f, top, 0f, 1920f, q);
                c.Save(); c.Scale(2); rc.Draw(c, 960f, top, 0f, 1920f, 2600); c.Restore();
            }
            c.Flush();
            SaveJpg(surf, Path.Combine(outDir, "04_race_control_bar_kinds.png"), new SKRectI(900, 80, 2940, 1060));
        }
    }

    // ------------------------------------------------------------------------------------------------ latest plate
    public static void LatestStills(string outDir, string bgPath, RaceSnapshot snap)
    {
        Directory.CreateDirectory(outDir);
        using var bg = SKImage.FromEncodedData(bgPath);
        using var surf = SKSurface.Create(new SKImageInfo(3840, 2160, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surf.Canvas;
        ViewerLatest.KeepOn = true;
        var events = new (string File, ViewerEvent E, bool WithRc)[]
        {
            ("05_latest_superchat", new ViewerEvent(ViewerKind.SuperChat, "Lap Chart Larry", "$5.00", "Great overtake into the Esses! Keep it clean through the Bus Stop, sending good vibes from Ohio", "youtube", false, true), false),
            ("06_latest_chat", new ViewerEvent(ViewerKind.Comment, "turn1_tifosi", "", "that BMW sounds unreal on the onboard", "youtube", false, true), false),
            ("07_latest_member_with_race_control", new ViewerEvent(ViewerKind.Member, "Kerb Hopper", "", "", "youtube", false, true, Months: 3), true),
            ("08_latest_bits", new ViewerEvent(ViewerKind.Bits, "PitWallPete", "500 bits", "clean stint!!", "twitch", false, true), false),
        };
        foreach (var (file, e, withRc) in events)
        {
            var st = new Stage();
            ViewerLatest.Note(e);
            if (withRc) RaceControlBar.PushSpoken("[[0]]Track limits are being monitored at the Ford Chicanes.[[end=8]]");
            c.Clear(); Bg(c, bg, 2);
            Play((cv, t) =>
            {
                PresentationState.IsBattleActive = false; PresentationState.Tick(snap);
                st.Plate.Tick(true, t);
                st.Tower.Draw(cv, 16f, 18f, 1080 - 34f, snap, t);
                if (withRc) st.Rc.Draw(cv, 960f, 110f, 1f, 1920f, t);
                st.Plate.Draw(cv, VpLayout.SessionBar, t);
                st.Bar1(cv, snap, t, SessionBarRenderer.BarWidth);
            }, 0, 2600, c, 2);
            SaveJpg(surf, Path.Combine(outDir, file + ".jpg"));
            SaveJpg(surf, Path.Combine(outDir, file + "_closeup.png"), new SKRectI(2860, 0, 3840, 220));
        }
    }

    // ------------------------------------------------------------------------------------------------ clips
    static Process Ffmpeg(string outFile, int fps) => Process.Start(new ProcessStartInfo("ffmpeg",
        $"-y -loglevel error -f rawvideo -pix_fmt rgba -s 1920x1080 -r {fps} -i - -c:v libx264 -preset medium -crf 22 -pix_fmt yuv420p -movflags +faststart \"{outFile}\"")
        { RedirectStandardInput = true, UseShellExecute = false })!;

    public static void RcClip(string outFile, string bgPath, RaceSnapshot snap)
    {
        using var bg = SKImage.FromEncodedData(bgPath);
        var info = new SKImageInfo(1920, 1080, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surf = SKSurface.Create(info);
        var c = surf.Canvas;
        var st = new Stage();
        using var ff = Ffmpeg(outFile, 60);
        var stdin = ff.StandardInput.BaseStream;
        var buf = new byte[info.BytesSize];
        RaceControlBar.Post(Decision(snap, RcCategory.Penalty, "DRIVE THROUGH PENALTY: TRACK LIMITS"));
        double len = 9000;
        float barW = SessionBarRenderer.BarWidth;
        for (double t = -800; t < len; t += 1000.0 / 60)
        {
            // a battle starts at 3.6 s: the bar grows to full width, the ticker comes in, the race-control bar glides down
            bool battle = t >= 3600;
            float k = (float)Math.Clamp((t - 3600) / 400.0, 0, 1); k = k * k * (3 - 2 * k);
            float w = barW + (1920 - barW) * k;
            c.Clear(); c.DrawImage(bg, new SKRect(0, 0, 1920, 1080));
            PresentationState.IsBattleActive = battle; PresentationState.Tick(snap);
            if (!battle) st.Tower.Draw(c, 16f, 18f, 1080 - 34f, snap, Math.Max(0, t));
            if (t >= 0) st.Rc.Draw(c, 960f, battle ? 154f : 110f, battle ? 0f : 1f, 1920f, t);
            if (battle) st.Ticker.Draw(c, 0f, 110f, 1920, snap, t);
            st.Bar1(c, snap, Math.Max(0, t), w);
            c.Flush();
            using var pm = surf.PeekPixels();
            pm.GetPixelSpan().CopyTo(buf);
            stdin.Write(buf, 0, buf.Length);
        }
        PresentationState.IsBattleActive = false;
        stdin.Close(); ff.WaitForExit();
        Console.WriteLine("ok " + outFile);
    }

    /// <summary>The cam placer on Angelo's "Racing" scene: the face cam (800x450 at 2992,1630 of the 3840x2160 canvas) and
    /// the pedal cam (512x288 at 3280,1310), drawn as labelled boxes in gold frames where OBS would show them. The very
    /// same CamPlacer runs every 150 ms of the clip's time; a move slides in five 50 ms steps as the OBS side sends them.</summary>
    public static void CamSim(string outFile, string bgPath, RaceSnapshot snap)
    {
        using var bg = SKImage.FromEncodedData(bgPath);
        var info = new SKImageInfo(1920, 1080, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surf = SKSurface.Create(info);
        var c = surf.Canvas;
        var st = new Stage();
        StoryDirector.CardMs = 5200;
        var face = new CamPlacer.Cam { Name = "FACE CAM", Home = SKRect.Create(2992, 1630, 800, 450) };
        var pedal = new CamPlacer.Cam { Name = "PEDAL CAM", Home = SKRect.Create(3280, 1310, 512, 288) };
        face.Alts = CamPlacer.Alternatives(face.Home, 3840, 2160, new[] { pedal.Home });
        pedal.Alts = CamPlacer.Alternatives(pedal.Home, 3840, 2160, new[] { face.Home });
        face.Cur = face.Home; pedal.Cur = pedal.Home;
        var cams = new[] { face, pedal };
        var slideFrom = new SKRect[2]; var slideAt = new double[] { -1e9, -1e9 };
        var log = new List<string>();
        using var ff = Ffmpeg(outFile, 30);
        var stdin = ff.StandardInput.BaseStream;
        var buf = new byte[info.BytesSize];
        double next = 0; bool forced = false;
        var gr = new List<SKRect>();
        string caption = "";
        using var boxP = new SKPaint { IsAntialias = true };
        using var txt = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 18f, Color = SKColors.White };
        using var small = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 15f, Color = new SKColor(255, 255, 255, 200) };
        using var dash = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(255, 216, 118, 150), PathEffect = SKPathEffect.CreateDash(new[] { 8f, 6f }, 0) };
        for (double t = 0; t < 13000; t += 1000.0 / 30)
        {
            // the story card arrives at 2.5 s (5.2 s on screen), the featured driver card is up when it isn't
            if (!forced && t >= 2500) { forced = true; StoryDirector.Force(StoryKind.RaceSoFar, t); }
            using (var nd = new SKNoDrawCanvas(1920, 1080)) { }
            PresentationState.IsBattleActive = false; PresentationState.Tick(snap);
            StoryDirector.Tick(snap, t, false);
            bool story = StoryDirector.Visible;
            if (t >= next)
            {
                next = t + 150;
                gr.Clear();
                void G(SKRect r) => gr.Add(new SKRect(r.Left * 2, r.Top * 2, r.Right * 2, r.Bottom * 2));
                G(new SKRect(16, 18, 448, 1064));                     // tower
                G(new SKRect(472, 18, 1448, 110)); G(new SKRect(890, -6, 1030, 134));   // session bar + watch
                if (story) G(new SKRect(1280f, 150f, 1904f, 1076f));   // story card (right-third layouts)
                else G(new SKRect(576, 853, 1344, 1064));               // featured driver card
                for (int i = 0; i < 2; i++)
                {
                    var before = cams[i].Cur;
                    var act = CamPlacer.Decide(cams, 2, i, gr.ToArray(), t, out var to);
                    if (act == CamPlacer.Act.Move)
                    {
                        slideFrom[i] = before; slideAt[i] = t;
                        CamPlacer.Moved(cams[i], to, t);
                        string what = CamPlacer.Near(to, cams[i].Home) ? "back home" : "moves clear";
                        log.Add($"{t / 1000:0.00}s {cams[i].Name} {what}");
                        caption = $"{t / 1000:0.0} s  {cams[i].Name} {what}";
                    }
                    else if (act == CamPlacer.Act.Raise) { cams[i].Raised = true; log.Add($"{t / 1000:0.00}s {cams[i].Name} raised"); }
                    else if (act == CamPlacer.Act.Lower) cams[i].Raised = false;
                }
            }
            c.Clear(); c.DrawImage(bg, new SKRect(0, 0, 1920, 1080));
            // the cams (under the overlay, as in OBS): home ghost, gold frame, box, label
            for (int i = 0; i < 2; i++)
            {
                var cam = cams[i];
                var h = Scale(cam.Home);
                if (!cam.AtHome) c.DrawRect(h, dash);
                float k = (float)Math.Clamp((t - slideAt[i]) / 250.0, 0, 1);
                k = MathF.Ceiling(k * 5f) / 5f; k = k * k * (3 - 2 * k);   // five OBS steps
                var a = Scale(slideFrom[i]); var b = Scale(cam.Cur);
                var r = k >= 1 ? b : SKRect.Create(a.Left + (b.Left - a.Left) * k, a.Top + (b.Top - a.Top) * k, b.Width, b.Height);
                boxP.Color = new SKColor(0xC9, 0xA9, 0x61); c.DrawRect(new SKRect(r.Left - 3, r.Top - 3, r.Right + 3, r.Bottom + 3), boxP);
                boxP.Color = i == 0 ? new SKColor(0x2A, 0x33, 0x48) : new SKColor(0x33, 0x2A, 0x3A); c.DrawRect(r, boxP);
                c.DrawText(cam.Name, r.Left + 12, r.Top + 28, txt);
                c.DrawText(i == 0 ? "VDO.Ninja 1280x720" : "S10 USB window", r.Left + 12, r.Top + 50, small);
            }
            // the overlay on top
            st.Tower.Draw(c, 16f, 18f, 1080 - 34f, snap, t);
            if (!story && StoryDirector.Current == StoryKind.None)
            {
                c.Save(); c.Translate((1920 - 768f) / 2f, 1080 - 16f - 211f); st.Featured.Draw(c, 24f, 32.5f, snap, t); c.Restore();
            }
            st.Story.Draw(c, 1920, 1080, snap, t);
            st.Bar1(c, snap, t, SessionBarRenderer.BarWidth);
            // caption (preview only)
            using (var bk = new SKPaint { Color = new SKColor(0, 0, 0, 170) }) c.DrawRect(0, 1040, 1920, 40, bk);
            c.DrawText("CAM PLACER PREVIEW  -  OBS scene \"Racing\" (boxes = where OBS shows the cams)   " + caption, 16, 1067, small);
            c.Flush();
            using var pm = surf.PeekPixels();
            pm.GetPixelSpan().CopyTo(buf);
            stdin.Write(buf, 0, buf.Length);
        }
        stdin.Close(); ff.WaitForExit();
        File.WriteAllLines(Path.ChangeExtension(outFile, ".txt"), log);
        foreach (var l in log) Console.WriteLine(l);
        Console.WriteLine("ok " + outFile);
    }

    static SKRect Scale(SKRect r) => new(r.Left / 2, r.Top / 2, r.Right / 2, r.Bottom / 2);

    // ------------------------------------------------------------------------------------------------ cost
    /// <summary>Per-frame CPU of the bar and the plate at 4K (no-draw canvas = the render thread's side) and the cam
    /// placer's decision cost.</summary>
    public static void Bench(RaceSnapshot snap)
    {
        var st = new Stage();
        ViewerLatest.Note(new ViewerEvent(ViewerKind.SuperChat, "Lap Chart Larry", "$5.00", "Great overtake into the Esses!", "youtube", false, true));
        using var nd = new SKNoDrawCanvas(3840, 2160);
        double tt = 0;
        // warm
        RaceControlBar.Post(Decision(snap, RcCategory.Penalty, "DRIVE THROUGH PENALTY: TRACK LIMITS"));
        for (int i = 0; i < 300; i++, tt += 16.7) { nd.Save(); nd.Scale(2); st.Rc.Draw(nd, 960, 110, 1, 1920, tt); st.Plate.Tick(true, tt); st.Plate.Draw(nd, VpLayout.SessionBar, tt); nd.Restore(); }
        long a0 = GC.GetAllocatedBytesForCurrentThread(); var sw = Stopwatch.StartNew();
        int N = 300;
        for (int i = 0; i < N; i++, tt += 16.7) { nd.Save(); nd.Scale(2); st.Rc.Draw(nd, 960, 110, 1, 1920, tt); nd.Restore(); }
        double rcMs = sw.Elapsed.TotalMilliseconds / N; long rcB = (GC.GetAllocatedBytesForCurrentThread() - a0) / N;
        a0 = GC.GetAllocatedBytesForCurrentThread(); sw.Restart();
        for (int i = 0; i < N; i++, tt += 16.7) { nd.Save(); nd.Scale(2); st.Plate.Tick(true, tt); st.Plate.Draw(nd, VpLayout.SessionBar, tt); nd.Restore(); }
        double plMs = sw.Elapsed.TotalMilliseconds / N; long plB = (GC.GetAllocatedBytesForCurrentThread() - a0) / N;
        a0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < N; i++, tt += 16.7) st.Plate.Tick(true, tt);
        Console.WriteLine($"  (plate tick alone: {(GC.GetAllocatedBytesForCurrentThread() - a0) / N} B/frame)");
        a0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < N; i++, tt += 16.7) { nd.Save(); nd.Scale(2); nd.Restore(); }
        Console.WriteLine($"  (canvas save/scale alone: {(GC.GetAllocatedBytesForCurrentThread() - a0) / N} B/frame)");
        // the placer: 20 graphics, two cams
        var face = new CamPlacer.Cam { Home = SKRect.Create(2992, 1630, 800, 450) }; face.Cur = face.Home;
        var pedal = new CamPlacer.Cam { Home = SKRect.Create(3280, 1310, 512, 288) }; pedal.Cur = pedal.Home;
        face.Alts = CamPlacer.Alternatives(face.Home, 3840, 2160, new[] { pedal.Home }); pedal.Alts = CamPlacer.Alternatives(pedal.Home, 3840, 2160, new[] { face.Home });
        var cams = new[] { face, pedal };
        var g = new SKRect[20];
        for (int i = 0; i < g.Length; i++) g[i] = SKRect.Create(100 + i * 150, 50 + i * 90, 300, 200);
        for (int i = 0; i < 1000; i++) { CamPlacer.Decide(cams, 2, 0, g, i, out _); CamPlacer.Decide(cams, 2, 1, g, i, out _); }
        sw.Restart();
        for (int i = 0; i < 100000; i++) { CamPlacer.Decide(cams, 2, 0, g, 5000, out _); CamPlacer.Decide(cams, 2, 1, g, 5000, out _); }
        double decUs = sw.Elapsed.TotalMilliseconds * 1000 / 100000;
        // the render thread's side, as OverlayCompositor.CollectCamRects does it: ~20 rects from VpLayout + the pack's
        // claims into a kept array, then copied under a lock (ObsCams.Publish)
        var dst = new SKRect[40]; var pub = new SKRect[48]; object gate = new();
        VpLayout.TickerVisible = true; VpLayout.BattleCardVisible = true; VpLayout.RadioVisible = true; VpLayout.StoryVisible = true;
        long ca = GC.GetAllocatedBytesForCurrentThread();
        sw.Restart();
        int M = 100000;
        for (int i = 0; i < M; i++)
        {
            int n = 0;
            void Add(SKRect r) { if (n < dst.Length && r.Width > 1 && r.Height > 1) dst[n++] = r; }
            Add(VpLayout.Tower); var b = VpLayout.SessionBar; Add(b); Add(new SKRect(b.MidX - 70f, b.MidY - 70f, b.MidX + 70f, b.MidY + 70f));
            if (VpLayout.TickerVisible) Add(VpLayout.Ticker);
            if (VpLayout.StoryVisible) Add(new SKRect(1280f, 150f, 1904f, 1076f));
            Add(StoryCards.MiniRect()); Add(VpLayout.Featured); if (VpLayout.BattleCardVisible) Add(VpLayout.BattleCard);
            Add(VpLayout.BattleMap); if (VpLayout.RadioVisible) Add(VpLayout.Radio); Add(VpLayout.Custom);
            Add(new SKRect(b.Left - 40f, b.Bottom - 4f, b.Right + 40f, b.Bottom + 170f));
            n += VpLayout.Claims(dst.AsSpan(n), tt);
            Add(st.Rc.NextRect); Add(ViewerLatestPlate.RectFor(b));
            lock (gate) { dst.AsSpan(0, n).CopyTo(pub); }
        }
        double colUs = sw.Elapsed.TotalMilliseconds * 1000 / M; long colB = (GC.GetAllocatedBytesForCurrentThread() - ca) / M;
        Console.WriteLine($"cam rect collection (render thread, every 150 ms): {colUs:0.00} us, {colB} B");
        Console.WriteLine($"race-control bar: {rcMs:0.000} ms/frame CPU (4K no-draw), {rcB} B/frame");
        Console.WriteLine($"latest plate:     {plMs:0.000} ms/frame CPU (4K no-draw), {plB} B/frame");
        Console.WriteLine($"cam placer decision (2 cams, 20 graphics): {decUs:0.00} us per collection");
    }
}
#endif
